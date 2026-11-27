"""Send masked survey answers to the big model and get features back.

Only masked text goes in (see mask.py). The exact request body is written to
out/sent_to_ai.json so anyone can check what left the machine.
"""
from __future__ import annotations

import json
import os
import re
import time
import urllib.error
import urllib.request
from pathlib import Path

# Gemini Flash-Lite on the owner's paid Google AI key (owner 2026-11-27 "use my Google API key and use a cheap Google
# model"). Replaces DeepSeek via Experiential: from 2026-11 Experiential answers every request that asks for zero data
# retention with 403 "part of the Pro plan", so no pass had written a mention since 11-05. Paid-tier Gemini API traffic
# is not used to train Google's models. Key: env GEMINI_API_KEY. Never commit it.
MODEL = os.environ.get("CLASSIFIER_MODEL", "gemini-3.5-flash-lite")
KEY_ENV = "GEMINI_API_KEY"
URL = "https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent"
RETRY_HTTP = {429, 500, 502, 503, 504}
RESPONSE_SCHEMA = {
    "type": "OBJECT",
    "properties": {"mentions": {"type": "ARRAY", "items": {
        "type": "OBJECT",
        "properties": {"id": {"type": "STRING"}, "feature": {"type": "STRING"},
                       "kind": {"type": "STRING", "enum": ["like", "dislike", "want"]}, "quote": {"type": "STRING"}},
        "required": ["id", "feature", "kind", "quote"]}}},
    "required": ["mentions"],
}


def _call(body: dict) -> dict:
    key = os.environ.get(KEY_ENV)
    if not key:
        raise RuntimeError(f"{KEY_ENV} is not set")
    req = urllib.request.Request(URL.format(model=MODEL), data=json.dumps(body).encode("utf-8"),
                                 headers={"x-goog-api-key": key, "Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.loads(r.read().decode("utf-8"))

SYSTEM = """You read survey answers about two government project-management software tools.
Some words are hidden behind codes like [PERSON_1], [ORG_2], [CONTRACT_1], [MONEY_1]. Never guess what they are. Copy a code exactly if you need it.

Your job: find every product feature or capability each answer talks about.
- Include features a complaint points at without naming them. "Anyone can edit any project, even interns" is about Role-based permissions. "The numbers are behind DIU's system" is about Data sync with DIU.
- When an answer fits one of the KNOWN TOPICS, use that exact topic name. Otherwise make a short 2-4 word feature name, like "PDF export" or "Single sign-on".
- Use the same name every time the same feature comes up, across all answers.
- One answer can touch more than one feature. Tag every one it touches, each as its own mention. Prefer the more specific KNOWN TOPIC over a general one.
- Never put a company or tool name in a feature name.
- How the tool itself behaves counts as a feature too: speed, crashes, login, layout, colors, clicks, mobile app, shortcuts, dark mode. Name it, e.g. "App stability", "Search speed", "Dark mode".
- Only skip things that are about the company or people, not the software: support staff, pricing, training, onboarding, documentation. Also skip messages with nothing about the product (contact details, deadlines, who asked what).
- A "want" answer is almost always asking for a feature. Tag it.
- quote: the exact words from the answer that mention it, copied character for character.
- kind: for boxes "like", "dislike", "want" copy the box. For box "comment" decide like, dislike, or want yourself.

Reply with JSON only, no other text:
{"mentions": [{"id": "<answer id>", "feature": "<name>", "kind": "like|dislike|want", "quote": "<exact words>"}]}"""


def extract(answers: list[dict], topics: list[str], seen: list[str], sent_log: Path) -> tuple[list[dict], dict]:
    """answers = [{"id", "box", "text"}] with text ALREADY masked. topics = the 12 form topics.
    seen = feature names made up on earlier runs. Returns (mentions, usage).
    Mentions whose quote isn't really in the answer are dropped."""
    user = ("KNOWN TOPICS (always check these first):\n" + "\n".join(f"- {t}" for t in topics) +
            ("\n\nNAMES USED BEFORE (reuse one only when no KNOWN TOPIC fits, instead of inventing a new "
             "name for the same thing):\n" + "\n".join(f"- {s}" for s in seen) if seen else "") +
            "\n\nANSWERS:\n" + json.dumps(answers, indent=1))
    body = {"systemInstruction": {"parts": [{"text": SYSTEM}]},
            "contents": [{"role": "user", "parts": [{"text": user}]}],
            # A schema forces valid JSON: without it Flash-Lite sometimes dropped a comma in a long list (2026-11-27 test).
            "generationConfig": {"responseMimeType": "application/json", "maxOutputTokens": 16000,
                                 "responseSchema": RESPONSE_SCHEMA}}
    sent_log.write_text(json.dumps({"model": MODEL, **body}, indent=1), encoding="utf-8")

    for attempt in range(1, 4):
        try:
            t0 = time.time()
            reply = _call(body)
            break
        except (urllib.error.URLError, TimeoutError) as e:
            code = getattr(e, "code", None)
            detail = e.read().decode("utf-8", "replace")[:300] if isinstance(e, urllib.error.HTTPError) else str(e)
            print(f"  AI call attempt {attempt} failed: {code} {detail}")
            if attempt == 3 or (code is not None and code not in RETRY_HTTP):
                raise RuntimeError(f"Gemini call failed: {code} {detail}") from e
            time.sleep(5 * attempt)
    parts = ((reply.get("candidates") or [{}])[0].get("content") or {}).get("parts") or []
    text = "".join(p.get("text", "") for p in parts if not p.get("thought"))
    m = re.search(r"\{.*\}", text, re.S)
    data = json.loads(m.group(0)) if m else {"mentions": []}

    by_id = {a["id"]: a for a in answers}
    kept, dropped = [], []
    for mn in data.get("mentions", []):
        a = by_id.get(mn.get("id"))
        quote = (mn.get("quote") or "").strip()
        if a and quote and quote.lower() in a["text"].lower() and mn.get("kind") in ("like", "dislike", "want"):
            kept.append(mn)
        else:
            dropped.append(mn)
    meta = reply.get("usageMetadata", {})
    usage = dict(input_tokens=meta.get("promptTokenCount"), output_tokens=meta.get("candidatesTokenCount"),
                 thinking_tokens=meta.get("thoughtsTokenCount"), model=MODEL, seconds=round(time.time() - t0, 1),
                 returned=len(data.get("mentions", [])), dropped_bad_quote=len(dropped))
    return kept, usage
