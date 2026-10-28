"""Strip names and confidential data out of survey text before any AI sees it.

Every sensitive value becomes a code like [PERSON_1]. The real values stay in the
Vault (a lookup table kept on our side only), so the admin view can swap them back.
After masking, the July 2026 EWARP gate (local_gate.py) re-scans the
text; anything it still flags is held back instead of sent.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import local_gate  # noqa: E402  PIID, money, email, phone detectors + spaCy PERSON

TOKEN_RE = re.compile(r"\[(?:PERSON|ORG|PROGRAM|CONTRACT|MONEY|EMAIL|PHONE|ID|IP)_\d+\]")

# detector name in local_gate -> our token kind
FORMAT_KINDS = {
    "piid-standalone": "CONTRACT", "contract-id": "CONTRACT",
    "money-words": "MONEY", "money-digits": "MONEY", "money-sign": "MONEY", "money-context": "MONEY",
    "email": "EMAIL", "email-obfuscated": "EMAIL", "phone": "PHONE", "ssn": "ID", "ip": "IP",
}
# spaCy labels we mask. ORG catches portfolio companies the term list doesn't know yet.
NER_KINDS = {"PERSON": "PERSON", "ORG": "ORG"}
# product words spaCy sometimes tags as ORG; masking them would hide the feature itself
FEATURE_WORDS = {"drive", "google drive", "salesforce", "excel", "pdf", "pdfs", "sso", "cac",
                 "piee", "pie", "sam.gov", "slack", "outlook", "word", "dark mode"}


class Vault:
    """Real value <-> code. Shared across a whole run so one person = one code everywhere."""

    def __init__(self) -> None:
        self.by_value: dict[str, str] = {}
        self.by_token: dict[str, dict] = {}
        self.counts: dict[str, int] = {}

    def token(self, kind: str, value: str) -> str:
        value = value.strip()
        key = value.lower()
        if key in self.by_value:
            return self.by_value[key]
        self.counts[kind] = self.counts.get(kind, 0) + 1
        tok = f"[{kind}_{self.counts[kind]}]"
        self.by_value[key] = tok
        self.by_token[tok] = {"kind": kind, "value": value}
        return tok

    def unmask(self, text: str) -> str:
        return TOKEN_RE.sub(lambda m: self.by_token.get(m.group(0), {}).get("value", m.group(0)), text)

    def save(self, path: Path) -> None:
        path.write_text(json.dumps(self.by_token, indent=1), encoding="utf-8")


def _replace_words(text: str, value: str, tok: str) -> str:
    return re.sub(rf"(?<![\w\[]){re.escape(value)}(?![\w\]])", tok, text, flags=re.I)


def _format_hits(text: str) -> list[tuple[str, str]]:
    """(kind, exact text to replace) for every format detector hit."""
    hits = []
    for name, rx in local_gate._DETECTORS:
        kind = FORMAT_KINDS.get(name)
        if not kind:
            continue
        for m in rx.finditer(text):
            hit = m.group(0).strip()
            if name == "money-words" and not local_gate._MONEY_WORDS_GUARD.search(hit):
                continue
            if name == "money-context":  # match includes "budget:"; replace only the number
                num = local_gate._MONEY_CONTEXT_NUM.search(hit)
                hit = num.group(0) if num else ""
            if name == "contract-id":  # match includes "contract number"; replace only the id
                idm = re.search(r"[A-Z0-9][A-Z0-9-]{4,}$", hit, re.I)
                hit = idm.group(0) if idm else ""
            if hit and not TOKEN_RE.fullmatch(hit):
                hits.append((kind, hit))
    # longest first so "$2.4M" goes before a bare "2.4M"
    return sorted(hits, key=lambda h: len(h[1]), reverse=True)


def _ner_hits(text: str) -> list[tuple[str, str]]:
    nlp = local_gate._spacy()
    if nlp is None:
        raise RuntimeError("spaCy en_core_web_lg is missing; refusing to mask without it")
    doc = nlp(text)
    hits = []
    for ent in doc.ents:
        kind = NER_KINDS.get(ent.label_)
        t = ent.text.strip().strip(".,:;'\"")
        if not kind or not t or TOKEN_RE.search(t) or t.lower() in FEATURE_WORDS:
            continue
        # spaCy's own word tagger disagrees: "Milestone dates..." is a NOUN, "Email me..." a VERB
        if not any(tok.pos_ == "PROPN" for tok in ent):
            continue
        if local_gate._is_allowlisted(t):
            continue
        hits.append((kind, t))
    # A proper noun spaCy has never seen ("Anduril's") is what company names and codenames
    # look like. NER missed exactly this on 2026-10-04, so mask them all.
    for tok in doc:
        t = tok.text.strip()
        inside_code = re.fullmatch(r"[A-Z]+_\d+", t) is not None  # spaCy splits "[ORG_2]" into "[", "ORG_2", "]"
        if (tok.pos_ == "PROPN" and tok.is_oov and t[:1].isupper() and not inside_code
                and t.lower() not in FEATURE_WORDS and not local_gate._is_allowlisted(t)):
            hits.append(("ORG", t))
    return sorted(set(hits), key=lambda h: len(h[1]), reverse=True)


def mask(text: str, vault: Vault, known: list[tuple[str, str]]) -> str:
    """known = [(kind, value)] from sensitive_terms.json plus the respondent's own name."""
    if not text:
        return ""
    # emails/phones/contracts first: a name inside an email ("s.grant@...") would otherwise
    # get coded first and break the email match (held back on the 2026-10-04 run)
    for kind, hit in _format_hits(text):
        if hit in text:  # a longer hit may already have swallowed it ("$2.4M" before "2.4M")
            text = text.replace(hit, vault.token(kind, hit))
    for kind, value in sorted(known, key=lambda k: len(k[1]), reverse=True):
        if value.strip():
            text = _replace_words(text, value, vault.token(kind, value))
    for kind, hit in _ner_hits(text):
        if re.search(rf"(?<![\w\[]){re.escape(hit)}(?![\w\]])", text, re.I):
            text = _replace_words(text, hit, vault.token(kind, hit))
    return text


def still_sensitive(masked: str) -> list[str]:
    """Run the July gate on masked text. Codes are swapped for a plain word first so
    the gate judges the words around them, not the codes. Empty list = safe to send."""
    plain = TOKEN_RE.sub("someone", masked)
    clean, findings = local_gate.gate_text(plain)
    return [] if clean else [f for f in findings if f.split(": ", 1)[-1].lower() not in GATE_FALSE_ALARMS]


# Verbs spaCy tags as PERSON when a sentence starts with them ("Email someone or call
# someone" -> PERSON "Email"). Seen 2026-10-04 on the first stripper test.
GATE_FALSE_ALARMS = {"email", "call", "text", "message", "ping", "ask", "tell"}


def name_parts(full_name: str) -> list[tuple[str, str]]:
    """'Marcus Bell' -> mask 'Marcus Bell', 'Marcus', and 'Bell'."""
    full = (full_name or "").strip()
    if not full:
        return []
    parts = [p for p in re.split(r"\s+", full) if len(p) > 1]
    return [("PERSON", full)] + [("PERSON", p) for p in parts]
