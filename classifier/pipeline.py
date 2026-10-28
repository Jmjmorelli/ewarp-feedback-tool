"""Google Form submissions -> strip sensitive data -> big AI finds features -> data for Shaq's map.

    python pipeline.py data/fake_submissions.json

Writes to out/:
  feedback.json         real text + features, the shape Shaq's frontend expects (src/lib/types.ts)
  feedback_masked.json  same, but with the codes the AI saw instead of real values
  vault.json            code -> real value. Never sent anywhere. Admin only.
  sent_to_ai.json       the exact request body that went to the AI
  run_report.json       counts, leak check, held-back answers, AI usage
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

import extract
import mask

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"
BOXES = ("like", "dislike", "want", "comment")
BOX_FIELD = {"like": "likes", "dislike": "dislikes", "want": "wants", "comment": "comments"}


def found(value: str, text: str) -> bool:
    """Whole-word, case-insensitive. 'Tom' must not match inside 'custom'."""
    return re.search(rf"(?<!\w){re.escape(value)}(?!\w)", text, re.I) is not None


def _plain(a):
    """Google Apps Script hands answers over as strings, or lists of strings (namedValues)."""
    if isinstance(a, list):
        a = ", ".join(str(x) for x in a if str(x).strip())
    return "" if a is None else a


def _title_words() -> dict:
    """forms.local.json ({"vendor1": ["word in the title"], ...}), never committed, since titles hold vendor names."""
    local = HERE / "forms.local.json"
    return json.loads(local.read_text(encoding="utf-8")) if local.exists() else {}


def vendor_for(form: str, vendor_by_form: dict, answers: dict | None = None) -> str | None:
    """`form` can be the key (vendor1) or the form's title as Joe's webhook sends it. The combined
    "Platform Feedback Survey" (2026-10-21) names both vendors, so there the vendor is the answer to
    "What platform are you answering the survey for?"."""
    if form in vendor_by_form:
        return vendor_by_form[form]
    words = _title_words()
    picked = [_plain(a) for q, a in (answers or {}).items() if "platform" in q.lower() and "answering" in q.lower()]
    for text in picked + [form]:
        for key, ws in words.items():
            if any(w.lower() in text.lower() for w in ws) and key in vendor_by_form:
                return vendor_by_form[key]
    return None


def _drop_vendor(q: str) -> str:
    """"How would you rate Vendor1's X?" ->"How would you rate X?", so both vendors' ratings share keys
    and no vendor name rides along in the output."""
    for ws in _title_words().values():
        for w in ws:
            q = re.sub(rf"\b{re.escape(w)}(?:'s|'|’s|’)?\s+", "", q, flags=re.I)
    return q


def normalize(sub: dict, vendor_by_form: dict, idx: int) -> dict:
    """One raw Google Form submission -> one response. Matches on the real question titles.
    Format: SURVEY_JSON.md."""
    answers = sub["answers"]
    submitted = sub.get("submittedAt") or _plain(answers.get("Timestamp", ""))
    r = {"id": str(sub.get("id") or f"r{idx:02d}"), "vendorId": vendor_for(sub["form"], vendor_by_form, answers), "submittedAt": submitted,
         "name": "", "role": "", "likes": "", "dislikes": "", "wants": "", "comments": "", "ratings": {}}
    for q, a in answers.items():
        a = _plain(a)
        ql = q.lower().replace("’", "'")  # Google Forms may use a curly apostrophe
        if ql in ("name", "full name", "your name"):
            r["name"] = a
        elif "role" in ql and "rate" not in ql:
            r["role"] = a
        elif "don't you like" in ql or "dont you like" in ql:
            r["dislikes"] = a
        elif "do you like" in ql:
            r["likes"] = a
        elif "additions" in ql:
            r["wants"] = a
        elif "additional comments" in ql:
            r["comments"] = a
        elif ql.startswith("how would you rate") or "rate your overall" in ql:
            if str(a).strip().isdigit():  # "3" from Apps Script -> 3; skipped optional ratings stay out
                r["ratings"][_drop_vendor(q)] = int(str(a).strip())
    role = r["role"].lower()
    r["group"] = "pm" if re.search(r"(program|project)\s*manager|\bpm\b", role) else "sponsor" if "sponsor" in role else "other"
    return r


def main(src: Path) -> None:
    OUT.mkdir(exist_ok=True)
    cfg = json.loads((HERE / "features.json").read_text(encoding="utf-8"))
    terms_file = HERE / "sensitive_terms.json"  # real list, never committed
    if not terms_file.exists():
        terms_file = HERE / "sensitive_terms.example.json"
    terms = json.loads(terms_file.read_text(encoding="utf-8"))["terms"]
    known = [(t["kind"], t["value"]) for t in terms]
    vendor_by_form = {v["form"]: v["id"] for v in cfg["vendors"]}
    subs = json.loads(src.read_text(encoding="utf-8"))
    responses = [normalize(s, vendor_by_form, i + 1) for i, s in enumerate(subs)]
    skipped = [s["form"] for s, r in zip(subs, responses) if r["vendorId"] is None]
    if skipped:
        print(f"skipped {len(skipped)} submission(s) from unknown forms: {sorted(set(skipped))}")
    responses = [r for r in responses if r["vendorId"] is not None]

    # 1. strip. Every respondent's own name is masked everywhere, not just in their answer.
    for r in responses:
        known += mask.name_parts(r["name"])
    vault = mask.Vault()
    answers, held = [], []
    for r in responses:
        r["masked"] = {}
        for box in BOXES:
            text = r[BOX_FIELD[box]]
            if not text.strip():
                continue
            m = mask.mask(text, vault, known)
            r["masked"][box] = m
            leftovers = mask.still_sensitive(m)
            if leftovers:
                held.append({"id": f"{r['id']}-{box}", "findings": leftovers})  # NOT sent
            else:
                answers.append({"id": f"{r['id']}-{box}", "box": box, "text": m})
    vault.save(OUT / "vault.json")
    print(f"stripped {len(vault.by_token)} sensitive values; {len(answers)} answers clean, {len(held)} held back")

    # 2. leak check on the exact payload BEFORE it goes out
    payload = json.dumps(answers)
    leaks = [v["value"] for v in vault.by_token.values() if found(v["value"], payload)]
    leaks += [r["name"] for r in responses if r["name"] and found(r["name"], payload)]
    if leaks:
        sys.exit(f"STOPPED: {len(leaks)} real values still in the payload: {leaks[:5]}")
    if "--dry" in sys.argv:
        (OUT / "would_send.json").write_text(json.dumps(answers, indent=1), encoding="utf-8")
        sys.exit("dry run: stripped payload written to out/would_send.json, nothing sent")

    # 3. big AI. New feature names from earlier runs go back in as known names, so
    #    "Navigation clicks" doesn't come back as "Clicks to navigate" next time.
    memory_path = HERE / "feature_memory.json"
    memory = json.loads(memory_path.read_text(encoding="utf-8")) if memory_path.exists() else []
    topics = [t["name"] for t in cfg["topics"]]
    mentions, usage = extract.extract(answers, topics, [m for m in memory if m not in topics],
                                      OUT / "sent_to_ai.json")
    print(f"AI: {usage}")
    for mn in mentions:
        name = mn["feature"].strip()
        if name not in topics and name not in memory:
            memory.append(name)
    memory_path.write_text(json.dumps(memory, indent=1), encoding="utf-8")

    # 4. features: known topics keep their vendor list; a 'like' proves that vendor has it
    offered = {t["name"].lower(): set(t["offeredBy"]) for t in cfg["topics"]}
    feats: dict[str, dict] = {}
    for mn in mentions:
        rid = mn["id"].rsplit("-", 1)[0]
        resp = next(r for r in responses if r["id"] == rid)
        key = mn["feature"].strip().lower()
        f = feats.setdefault(key, {"id": f"f{len(feats) + 1}", "name": mn["feature"].strip(),
                                    "known": key in offered, "vendorIds": set(offered.get(key, set()))})
        if mn["kind"] == "like":
            f["vendorIds"].add(resp["vendorId"])
        resp.setdefault("mentions", [])
        entry = {"featureId": f["id"], "kind": mn["kind"], "quote": mn["quote"]}
        if not any(e["featureId"] == f["id"] and e["kind"] == mn["kind"] for e in resp["mentions"]):
            resp["mentions"].append(entry)

    def build(real: bool) -> dict:
        def show(s: str) -> str:
            return vault.unmask(s) if real else s
        out_resp = []
        for r in responses:
            mk = r["masked"]
            out_resp.append({
                "id": r["id"], "group": r["group"], "role": r["role"], "vendorId": r["vendorId"], "submittedAt": r["submittedAt"],
                **({"name": r["name"]} if real else {}),
                "likes": r["likes"] if real else mk.get("like", ""),
                "dislikes": r["dislikes"] if real else mk.get("dislike", ""),
                "wants": r["wants"] if real else mk.get("want", ""),
                "comments": r["comments"] if real else mk.get("comment", ""),
                "ratings": r["ratings"],
                "mentions": [dict(e, quote=show(e["quote"])) for e in r.get("mentions", [])],
            })
        return {
            "vendors": [{"id": v["id"], "name": v["name"]} for v in cfg["vendors"]],
            "features": [{"id": f["id"], "name": show(f["name"]), "known": f["known"],
                          "vendorIds": sorted(f["vendorIds"])} for f in feats.values()],
            "responses": out_resp,
        }

    (OUT / "feedback.json").write_text(json.dumps(build(True), indent=1), encoding="utf-8")
    (OUT / "feedback_masked.json").write_text(json.dumps(build(False), indent=1), encoding="utf-8")

    # 5. leak check again on what actually went out
    sent = (OUT / "sent_to_ai.json").read_text(encoding="utf-8")
    sent_leaks = [v["value"] for v in vault.by_token.values() if found(v["value"], sent)]
    report = {"responses": len(responses), "answers_sent": len(answers), "answers_held": held,
              "sensitive_values_stripped": len(vault.by_token),
              "real_values_found_in_sent_request": sent_leaks,
              "features": len(feats), "new_features": sum(not f["known"] for f in feats.values()),
              "mentions": len(mentions), "ai": usage}
    (OUT / "run_report.json").write_text(json.dumps(report, indent=1), encoding="utf-8")
    print(f"LEAK CHECK: {len(sent_leaks)} of {len(vault.by_token)} real values found in sent_to_ai.json")
    print(f"{len(feats)} features ({report['new_features']} new), {len(mentions)} mentions -> out/feedback.json")


if __name__ == "__main__":
    main(Path(sys.argv[1] if len(sys.argv) > 1 else HERE / "data" / "fake_submissions.json"))
