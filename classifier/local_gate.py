#!/usr/bin/env python3
"""
Copied from Documents/code/ewarp-tools/local_gate.py (the July 2026 EWARP gate) so this repo runs on its own.

local_gate.py — the SINGLE, 100%-LOCAL sanitization gate.

Every byte that could leave this machine for a cloud model must pass gate_text()
FIRST, on-device. Nothing here calls the cloud. Deny-by-default: a finding means
the caller MUST NOT publish/send (fail closed).

Catches what the upstream known-name SCRUB_MAP + LLM leak-check miss:
  - UNKNOWN person names            -> spaCy PERSON NER (REQUIRED; unavailable = finding)
  - project codenames / product     -> GLiNER (best-effort, local-only; bonus only)
  - money (digits + words)          -> deterministic detectors
  - phones / emails / SSNs / IPs    -> deterministic detectors
  - contract / CAGE / UEI / PIID    -> contextual deterministic detector

Hardening (Codex/Fable/Sol review, 2026-07-17):
  * spaCy is a REQUIRED detector: if it can't load, gate_text returns a finding so
    the caller quarantines (never silently publishes with detection off).
  * The WHOLE text is scanned in chunks — no truncation of a security scan.
  * GLiNER loads local-files-only (no network) and is best-effort bonus coverage.
  * Verdict acceptance is exact (leakcheck_ok), never a loose prefix.
"""
from __future__ import annotations

import re

# ── Allowlist: sanitized role labels + known-safe vocabulary are EXPECTED; a NER
#    hit on these is a false alarm. Lowercase. Kept in sync with pipeline.py.
SAFE_TERMS = {
    "diu", "ucf", "salesforce", "jira", "dechante", "vendor", "vendor 1", "vendor 2",
    "il5", "il-5", "il4", "il-4", "io5", "fedramp", "sharepoint", "teams", "sql", "azure",
    "react", "python", "docker", "word", "excel", "powerpoint", "outlook", "gmail",
    "google", "meet", "zoom", "microsoft", "aws", "box", "obsidian", "kanban", "trace",
    "congress", "congressional", "osd", "tiu", "dc", "pm", "pms", "navy",
    "marine", "corps", "marine corps", "ewarp", "ework", "cso", "pet", "cdao", "igce",
    "ai", "llm", "cui", "gcc", "government", "capstone", "guardians", "space force",
    "sam.gov", "congress.gov", "federal register", "gao", "ogr", "clin", "rfi", "rfp",
    "america", "american", "united states", "us", "usa", "dod", "senator", "guardian",
    # common safe acronyms/vocab (were previously waved through by a blanket [A-Z]{2,5}
    # rule that also let real 2-5 letter codenames pass — that rule is now removed)
    "api", "pdf", "csv", "json", "html", "url", "ceo", "cfo", "cto", "coo", "poc",
    "mvp", "ux", "ui", "crm", "cage", "uei", "piid", "bpa", "sow", "pws", "aoi",
    "trl", "mipr", "sal", "ao", "rdt", "coop", "faq", "tldr", "id", "ok", "ip",
    "fde", "rpp", "rfp", "usd", "eur", "gbp", "qfr", "qfrs", "iot", "gpu", "cpu",
    # tool/model names spaCy tags as PERSON — the QA boilerplate says "safe for cloud
    # Claude to read", and auto-heal turned that into "[name redacted]" (2026-07-25)
    "claude", "qwen", "whisper", "ollama", "gliner", "spacy", "obsidian",
    # meeting-UI vocabulary spaCy tags as PERSON — auto-heal turned "Note Taker" into
    # "[name redacted] Taker" across the 2026-07-23 report (2026-07-25)
    "note", "notes", "taker", "note taker", "notetaker", "meet", "sync", "recording",
}

# Sanitized role labels the report is SUPPOSED to contain. Whole-string, case-insensitive.
_ROLE_LABEL_RE = re.compile(
    r"^(person\s+[a-z]?\d?|diu\s+pm\s*[a-z]?|diu\s+(stakeholder|logistics|data\s+scientist|director)\s*[a-z]?|"
    r"vendor\s+\d(\s+(ceo|engineer|notetaker))?|capstone\s+team|teammate\s+[a-z]?|stakeholder\s+[a-z]?|"
    r"company\s+[xy]|dechante|meeting\s+owner|demo\s+presenter|notetaker|"
    r"team\s+member\s+[a-z]?|a\s+(senator|teammate|diu\s+pm|large\s+tech\s+company))$", re.I)

# Only these EXACT bracketed placeholders are allowed (what the scrub emits). Exact-set,
# so a bracketed token carrying a real name/codename inside is NOT allowlisted (finding).
_ALLOWED_PLACEHOLDERS = {
    "[name redacted]", "[redacted]", "[email]", "[phone]", "$[amount]", "[amount]",
    "[amount redacted]", "[site]", "[program reference redacted]", "[project name]",
    "[product name]", "[internal environment]", "[hosting platform]",
    "[contract number redacted]",
}

# A DoD PIID is self-identifying by SHAPE and needs no nearby keyword: 6-char DoDAAC,
# 2-digit FY, type code, 4-digit serial (e.g. A1B2C3-25-9-0000, made-up), optionally followed by
# a modification number (P00001…). The context-dependent "contract-id" detector below
# MISSED exactly this on 2026-07-23 because the number appeared bare inside a filename
# on a shared screen, and the report then declared "no sensitive data".
# serial is 4 ALPHANUMERIC, not 4 digits — a real CSO-style number like X12345-20-S-C001 has a
# letter in it and was missed by a digits-only pattern.
_PIID_RE = re.compile(
    r"\b[A-Z][A-Z0-9]{5}-\d{2}-[A-Z0-9]{1,2}-[A-Z0-9]{4}(?:[-\s]+P\d{5}[A-Z0-9]*)?\b")

# GLiNER hunts NAME/CODENAME-shaped labels only (broad ORG/LOCATION labels
# false-positive on ordinary nouns). GLiNER is bonus-only, so this can be generous.
_GLINER_LABELS = ["person", "presenter name", "project codename", "program name", "product name"]

# spaCy: PERSON only — the reliable signal for unknown-name leaks. ORG/GPE are too
# noisy to gate on ("Budget", "Data"); real org names are handled upstream by SCRUB_MAP.
_SPACY_SENSITIVE = {"PERSON"}

# ── deterministic format detectors ───────────────────────────────────────────

_NUM_WORDS = (r"(?:zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|"
              r"thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|"
              r"thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|thousand|million|"
              r"billion|trillion|point|and|a|half|dozen)")

# NOTE: "hundred" is deliberately NOT a money tail — "five hundred users" is a count,
# not money. "five hundred dollars" still matches via the "dollars" tail.
_MONEY_TAIL = r"(?:thousand|million|billion|trillion|grand|dollars?|bucks|cents?)"

_DETECTORS = [
    # money in words: "seven million dollars", "fifty thousand", "five bucks", "2.5 million"
    ("money-words", re.compile(r"\b(?:" + _NUM_WORDS + r"[\s-]+){1,6}" + _MONEY_TAIL + r"\b", re.I)),
    # money digits w/ magnitude/currency suffix: "5 grand", "3.2M", "50 thousand", ".5m"
    ("money-digits", re.compile(
        r"(?<![\w.])\.?\d[\d,]*(?:\.\d+)?\s*(?:k|m|b|thousand|million|billion|grand|dollars?|usd|cents?)\b", re.I)),
    # currency-prefixed: "$50,000", "USD 500", "€1.2M", "£5k"
    ("money-sign", re.compile(r"(?:\$|€|£|USD|EUR|GBP)\s?:?\s?\.?\d[\d,]*(?:\.\d+)?\s*(?:k|m|b|million|billion|thousand)?", re.I)),
    # contextual money: a money-ish number (comma-grouped, 4+ digits, or decimal) near a
    # money keyword — catches "budget: 50,000" that has no currency symbol
    ("money-context", re.compile(
        r"\b(?:budget|funding|fund|cost|price|obligation|expenditure|ceiling|"
        r"contract\s+value|worth|dollar)\b[^.\n]{0,25}?(?:\d{1,3}(?:,\d{3})+|\d{4,}|\d+\.\d+)", re.I)),
    ("email", re.compile(r"[\w.+-]+@[\w-]+\.[\w.]+")),
    ("email-obfuscated", re.compile(r"\b[\w.+-]+\s+(?:at|\[at\]|\(at\))\s+[\w.-]+\s+(?:dot|\[dot\]|\(dot\))\s+\w+", re.I)),
    # phone: separators optional -> also catches (555)123-4567 and 5551234567
    ("phone", re.compile(r"(?<!\d)(?:\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}(?!\d)")),
    ("ssn", re.compile(r"\b\d{3}-\d{2}-\d{4}\b")),
    ("ip", re.compile(r"\b(?:\d{1,3}\.){3}\d{1,3}\b")),
    # contract / solicitation / CAGE / UEI / PIID — WITH a nearby context keyword
    # standalone PIID — no context keyword required (see _PIID_RE note above)
    ("piid-standalone", _PIID_RE),
    ("contract-id", re.compile(
        r"\b(?:contract|award|task\s+order|delivery\s+order|solicitation|piid|bpa|"
        r"cage(?:\s+code)?|uei|clin)\b\s*(?:no\.?|number|#|:|=|\.)?\s*(?=[A-Z0-9-]*\d)[A-Z0-9][A-Z0-9-]{4,}", re.I)),
]

# money-words needs a real number-word (not bare "million records"); require a
# leading number-word before the tail so ordinary prose numbers are less noisy.
_MONEY_WORDS_GUARD = re.compile(r"\b" + _NUM_WORDS + r"[\s-]+" + _MONEY_TAIL, re.I)


# money categories that can be substituted wholesale. "money-context" is handled
# separately: its match spans the surrounding keyword ("budget: 50,000"), so a blind
# substitution would eat real words.
_MONEY_SUBSTITUTABLE = {"money-words", "money-digits", "money-sign"}
_MONEY_CONTEXT_NUM = re.compile(r"(?:\d{1,3}(?:,\d{3})+|\d{4,}|\d+\.\d+)")


def redact_money(text: str) -> str:
    """Replace every money form THIS gate detects with the allowed $[amount] token.

    Lives here, next to the detectors, so the upstream scrubber can never drift
    weaker than the gate. A '$'-only scrub used to miss "30 million" / "3.2M",
    which then tripped the gate and quarantined an otherwise-clean report.
    """
    def _amount(m: "re.Match[str]") -> str:
        # the detectors may swallow a trailing space ("USD 750 per" -> "USD 750 ");
        # put it back so the report doesn't read "$[amount]per".
        return "$[amount]" + (" " if m.group(0)[-1:].isspace() else "")

    for name, rx in _DETECTORS:
        if name in _MONEY_SUBSTITUTABLE:
            if name == "money-words":
                text = rx.sub(
                    lambda m: _amount(m) if _MONEY_WORDS_GUARD.search(m.group(0)) else m.group(0),
                    text)
            else:
                text = rx.sub(_amount, text)
        elif name == "money-context":
            # keep the keyword, redact only the number after it
            text = rx.sub(lambda m: _MONEY_CONTEXT_NUM.sub("$[amount]", m.group(0)), text)
    return text


def redact_ner_persons(text: str) -> tuple[str, int]:
    """Replace every non-allowlisted spaCy PERSON with [name redacted].

    Lets an unknown-name leak be fixed on-device instead of parking the whole
    report in quarantine. Returns (text, count). Deliberately does NOT touch
    GLiNER hits — those are bonus-only and noisy. If spaCy can't load, this is a
    no-op and the gate still fails closed, so nothing publishes undetected.
    """
    nlp = _spacy()
    if nlp is None:
        return text, 0
    names: set[str] = set()
    for chunk in _chunks(text, 80000, overlap=200):
        try:
            doc = nlp(chunk)
        except Exception:
            return text, 0  # inference failure -> change nothing, let the gate quarantine
        for ent in doc.ents:
            if ent.label_ in _SPACY_SENSITIVE and not _is_allowlisted(ent.text):
                t = ent.text.strip()
                if t:
                    names.add(t)
    if not names:
        return text, 0
    # longest first so "John Smith" is replaced before a bare "John"
    for name in sorted(names, key=len, reverse=True):
        text = re.sub(rf"\b{re.escape(name)}\b", "[name redacted]", text)
    return text, len(names)


def redact_contract_ids(text: str) -> str:
    """Replace shape-identifiable DoD contract numbers (PIIDs) with a placeholder."""
    return _PIID_RE.sub("[contract number redacted]", text)


def redact_phone(text: str) -> str:
    """Replace phone numbers using the gate's own pattern (separators optional)."""
    for name, rx in _DETECTORS:
        if name == "phone":
            return rx.sub("[phone]", text)
    return text


# ollama-style model tags ("qwen3:8b", "qwen2.5vl:7b") look like money to the
# money-digits detector because "8b" is a digit + magnitude letter. The pipeline
# stamps these into every report header, so without this the gate quarantines its
# own provenance line.
# Deliberately narrow: the name before the colon must ITSELF contain a digit, which
# every ollama tag does (qwen3, qwen2.5vl, llama3) and no money phrase does. That
# keeps "award:5m" / "budget:2b" firing as money. A digit-less model name
# ("mistral:7b") is NOT exempt and will quarantine — fail-closed, the safe direction.
_MODEL_TAG_RE = re.compile(r"\b[a-z][\w.\-]*\d[\w.\-]*:\d+(?:\.\d+)?[kmb]\b", re.I)


def find_format_pii(text: str) -> list[str]:
    findings = []
    tag_spans = [m.span() for m in _MODEL_TAG_RE.finditer(text)]
    for name, rx in _DETECTORS:
        for m in rx.finditer(text):
            hit = m.group(0).strip()
            if name == "money-words" and not _MONEY_WORDS_GUARD.search(hit):
                continue
            if name == "money-digits" and any(s <= m.start() and m.end() <= e
                                              for s, e in tag_spans):
                continue  # part of a model tag, not money
            if name == "ip":  # skip version strings like "Version 1.2.3.4"
                pre = text[max(0, m.start() - 12):m.start()].lower()
                if re.search(r"\bversion\s*$|\bv\.?\s*$", pre):
                    continue
            findings.append(f"{name}: {hit[:60]}")
    return findings


# ── chunking (never truncate a security scan) ────────────────────────────────

def _chunks(text: str, size: int, overlap: int) -> list[str]:
    if len(text) <= size:
        return [text]
    out, i = [], 0
    step = max(1, size - overlap)
    while i < len(text):
        out.append(text[i:i + size])
        i += step
    return out


# ── NER (lazy-loaded) ────────────────────────────────────────────────────────

_NLP = "unset"
_GLINER = "unset"


def _spacy():
    global _NLP
    if _NLP == "unset":
        try:
            import spacy
            _NLP = spacy.load("en_core_web_lg")
        except Exception:
            _NLP = None
    return _NLP


def _gliner():
    global _GLINER
    if _GLINER == "unset":
        try:
            from gliner import GLiNER
            # local-only: never touch the network from a CUI machine
            _GLINER = GLiNER.from_pretrained("urchade/gliner_medium-v2.1", local_files_only=True)
        except Exception:
            _GLINER = None
    return _GLINER


def ner_available() -> bool:
    """True when the REQUIRED detector (spaCy PERSON) is loaded."""
    return _spacy() is not None


def _is_allowlisted(term: str) -> bool:
    t = term.strip().strip(".,:;\"'").strip()
    if not t:
        return True
    if t.lower() in _ALLOWED_PLACEHOLDERS:
        return True
    t = t.strip("()[]").strip()
    if not t:
        return True
    if _ROLE_LABEL_RE.match(t):
        return True
    low = t.lower()
    if low in SAFE_TERMS:
        return True
    if all(w.lower() in SAFE_TERMS for w in low.split()):
        return True
    return False


def find_ner_entities(text: str) -> list[str]:
    findings = []
    nlp = _spacy()
    if nlp is None:
        # REQUIRED detector missing -> this is itself a finding => caller quarantines
        findings.append("REQUIRED-DETECTOR-UNAVAILABLE: spaCy en_core_web_lg not loaded "
                        "— cannot verify unknown names; fail closed")
    else:
        for chunk in _chunks(text, 80000, overlap=200):
            try:
                doc = nlp(chunk)
            except Exception as e:  # a per-chunk inference failure must fail closed too
                findings.append(f"REQUIRED-DETECTOR-UNAVAILABLE: spaCy inference error "
                                f"({type(e).__name__}); fail closed")
                continue
            for ent in doc.ents:
                if ent.label_ in _SPACY_SENSITIVE and not _is_allowlisted(ent.text):
                    findings.append(f"ner-spacy[{ent.label_}]: {ent.text[:50]}")
    gl = _gliner()  # best-effort bonus; absence is NOT a finding (spaCy is the required one)
    if gl is not None:
        for chunk in _chunks(text, 1500, overlap=100):
            try:
                for ent in gl.predict_entities(chunk, _GLINER_LABELS, threshold=0.6):
                    if not _is_allowlisted(ent["text"]):
                        findings.append(f"ner-gliner[{ent['label']}]: {ent['text'][:50]}")
            except Exception:
                pass
    return findings


# ── the single gate ──────────────────────────────────────────────────────────

def gate_text(text: str) -> tuple[bool, list[str]]:
    """Return (is_clean, findings). is_clean is True ONLY when no detector fired
    AND the required detector was available. Deny by default."""
    findings = find_format_pii(text) + find_ner_entities(text)
    seen, uniq = set(), []
    for f in findings:
        if f not in seen:
            seen.add(f)
            uniq.append(f)
    return (len(uniq) == 0, uniq)


def leakcheck_ok(verdict: str) -> bool:
    """Exact acceptance of the upstream LLM leak-check verdict. Only a bare 'CLEAN'
    or the known false-alarm form 'CLEAN (…)' passes — never 'MANUAL REVIEW', 'LEAKS:',
    a timeout, or any other status (fail closed)."""
    v = (verdict or "").strip()
    if v.upper() == "CLEAN":
        return True
    # the ONLY allowed annotated form: a single-line "CLEAN (…)" with balanced parens
    # that is NOT secretly carrying LEAKS/MANUAL/REVIEW text or a colon
    if re.fullmatch(r"CLEAN \([^()\n]*\)", v) and not re.search(r"LEAK|MANUAL|REVIEW|:", v.upper()):
        return True
    return False


if __name__ == "__main__":
    import sys
    src = sys.stdin.read() if not sys.argv[1:] else open(sys.argv[1], encoding="utf-8").read()
    clean, fs = gate_text(src)
    print(f"ner_available={ner_available()}  clean={clean}  findings={len(fs)}")
    for f in fs:
        print("  -", f)
    sys.exit(0 if clean else 2)
