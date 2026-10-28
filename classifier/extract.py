"""Send masked survey answers to the big model and get features back.

Only masked text goes in (see mask.py). The exact request body is written to
out/sent_to_ai.json so anyone can check what left the machine.
"""
from __future__ import annotations

import json
import re
import time
from pathlib import Path

import os

import anthropic

MODEL = "claude-sonnet-5"
# Sonnet through RightCodes /claude-aws (owner 2026-10-28, replaces the direct key of 10-23).
# Key: env RIGHTCODES_API_KEY (claude-main, Life OS/Secrets/RightCodes.md). Never commit it.
BASE_URL = "https://right.codes/claude-aws"
KEY_ENV = "RIGHTCODES_API_KEY"


def _client() -> anthropic.Anthropic:
    key = os.environ.get(KEY_ENV)
    if not key:
        raise RuntimeError(f"{KEY_ENV} is not set")
    return anthropic.Anthropic(base_url=BASE_URL, api_key=key)

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
    # no temperature: the API rejects sampling settings since 2026-10-21
    body = {"model": MODEL, "max_tokens": 16000, "system": SYSTEM,
            "messages": [{"role": "user", "content": user}]}
    sent_log.write_text(json.dumps(body, indent=1), encoding="utf-8")

    for attempt in range(1, 4):
        try:
            t0 = time.time()
            reply = _client().messages.create(**body).to_dict()
            break
        except (anthropic.APIConnectionError, anthropic.RateLimitError, anthropic.InternalServerError) as e:
            print(f"  AI call attempt {attempt} failed: {e}")
            if attempt == 3:
                raise
            time.sleep(5)
    text = "".join(c.get("text", "") for c in reply["content"] if c.get("type") == "text")
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
    usage = dict(reply.get("usage", {}), seconds=round(time.time() - t0, 1),
                 returned=len(data.get("mentions", [])), dropped_bad_quote=len(dropped))
    return kept, usage
