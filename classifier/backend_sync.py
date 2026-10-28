"""Joe's backend database -> strip sensitive data -> big AI finds features -> features + mentions back in the same database.

    set DATABASE_URL=postgresql://user:pass@host:5432/db   (Joe's Postgres; never commit it)
    python backend_sync.py           # classify every response it hasn't read yet, then stop
    python backend_sync.py --dry     # strip + leak check only: nothing sent, nothing written
    python backend_sync.py --watch   # automatic: check for new answers every 30 s, forever

Automatic (owner 2026-10-28 "make it automatic", replaces the one-run rule of 2026-10-23), and it runs on
the team server (owner 2026-10-28 "make everything run through the server they setup") as systemd service
ewarp-classifier, installed by .github/workflows/deploy.yml.
AI key: RIGHTCODES_API_KEY = a separate capped RightCodes claude-aws key just for the server (owner's pick),
never his main key. Secrets live only in the server's env file, never in this public repo:
    CLASSIFIER_STATE_DIR   where the done-list and run logs go (default: ./out next to this file)
    SENSITIVE_TERMS_FILE   real names to hide (default: sensitive_terms.json, else the example)

Joe's ResponseAnalysisWorker stamps analyzed_at within 10 s without reading anything, so this keeps
its own list of what it has read (classifier_done.json next to this file; the app's database user can't create tables). It only adds rows: features, mentions, and a
vendor_offerings row (vendor has this feature) for every like or dislike, using the vendor the survey
answer was about (owner 2026-10-28, Shaq: "point to what vendor they did the survey on"). A want stays
a gap: it means the vendor doesn't have it yet. It never edits or deletes his rows.
Tables it uses: docs/API.md "Data model" in ShaqMiller/ewarp-feedback-tool.
"""
from __future__ import annotations

import json
import os
import sys
import time
import uuid
from pathlib import Path

import psycopg

import extract
import mask
from pipeline import found

HERE = Path(__file__).resolve().parent
OUT = Path(os.environ.get("CLASSIFIER_STATE_DIR") or HERE / "out")
BOXES = (("like", "likes"), ("dislike", "dislikes"), ("want", "wants"), ("comment", "comment"))
NAME_KEYS = ("full name", "name", "your name")
COMMENT_PREFIX = "please provide any additional comments"  # Joe's responses table has no column for it


def _answers(answers) -> dict:
    return json.loads(answers) if isinstance(answers, str) else (answers or {})


def _comment(answers) -> str:
    for q, a in _answers(answers).items():
        if q.strip().lower().startswith(COMMENT_PREFIX) and isinstance(a, str):
            return a
    return ""


def _known_terms() -> list[tuple[str, str]]:
    f = Path(os.environ.get("SENSITIVE_TERMS_FILE") or HERE / "sensitive_terms.json")  # real list, never committed
    if not f.exists():
        f = HERE / "sensitive_terms.example.json"
    return [(t["kind"], t["value"]) for t in json.loads(f.read_text(encoding="utf-8"))["terms"]]


def _respondent_name(answers) -> str:
    for q, a in _answers(answers).items():
        if q.strip().lower() in NAME_KEYS:
            return ", ".join(a) if isinstance(a, list) else str(a or "")
    return ""


DONE = (OUT / "classifier_done.json") if os.environ.get("CLASSIFIER_STATE_DIR") else HERE / "classifier_done.json"
# {response_id: {"mentions": n, "note": ...}}


def load_done() -> dict:
    return json.loads(DONE.read_text(encoding="utf-8")) if DONE.exists() else {}


def pending(c: psycopg.Connection, done: dict, limit: int = 50) -> list[dict]:
    rows = c.execute("""
        select r.id, r.project_id, r.vendor_id, r.likes, r.dislikes, r.wants, s.answers
        from responses r left join survey_submissions s on s.id = r.raw_submission_id
        where not (r.id = any(%s))
        order by r.submitted_at limit %s""", (list(done), limit)).fetchall()
    keys = ("id", "project_id", "vendor_id", "likes", "dislikes", "wants", "answers")
    out = [dict(zip(keys, r)) for r in rows]
    for r in out:
        r["comment"] = _comment(r["answers"])
    return out


def run_project(c: psycopg.Connection, project_id: str, responses: list[dict], dry: bool, done: dict) -> dict:
    topics = [t["name"] for t in json.loads((HERE / "features.json").read_text(encoding="utf-8"))["topics"]]
    rows = c.execute("select id, name from features where project_id = %s", (project_id,)).fetchall()
    existing = {name.lower(): fid for fid, name in rows}
    seen = [name for _, name in rows]  # names the project already has, so the AI reuses them

    # 1. strip. Each respondent's own name is masked everywhere, not just in their answer.
    known = _known_terms()
    names = [_respondent_name(r["answers"]) for r in responses]
    for n in names:
        known += mask.name_parts(n)
    vault = mask.Vault()
    answers, held = [], []
    for r in responses:
        for box, field in BOXES:
            text = r[field] or ""
            if not text.strip():
                continue
            m = mask.mask(text, vault, known)
            if mask.still_sensitive(m):
                held.append(f"{r['id']}-{box}")  # NOT sent
            else:
                answers.append({"id": f"{r['id']}-{box}", "box": box, "text": m})

    # 2. leak check on the exact payload BEFORE it goes out
    payload = json.dumps(answers)
    leaks = [v["value"] for v in vault.by_token.values() if found(v["value"], payload)]
    leaks += [n for n in names if n and found(n, payload)]
    if leaks:
        raise SystemExit(f"STOPPED: {len(leaks)} real values still in the payload for {project_id}")
    report = {"project": project_id, "responses": len(responses), "answers_sent": len(answers),
              "answers_held": held, "stripped": len(vault.by_token)}
    if dry:
        (OUT / "backend_would_send.json").write_text(json.dumps(answers, indent=1), encoding="utf-8")
        return report

    # 3. big AI
    mentions, usage = (extract.extract(answers, topics, [s for s in seen if s not in topics],
                                       OUT / "backend_sent_to_ai.json") if answers else ([], {}))
    report["ai"] = usage

    # 4. write back: one feature per name per project, one mention per (response, feature, kind)
    count = {r["id"]: 0 for r in responses}
    new_features = 0
    with c.transaction():
        for mn in mentions:
            name = mn["feature"].strip()
            if mask.TOKEN_RE.search(name):  # a code in a feature name would leak to the screen as [ORG_1]
                continue
            rid = mn["id"].rsplit("-", 1)[0]
            fid = existing.get(name.lower())
            if fid is None:
                fid = "ftr_" + uuid.uuid4().hex[:12]
                c.execute("insert into features (id, project_id, name, created_at) values (%s, %s, %s, now())",
                          (fid, project_id, name))
                existing[name.lower()] = fid
                new_features += 1
            dup = c.execute("select 1 from mentions where response_id = %s and feature_id = %s and kind = %s",
                            (rid, fid, mn["kind"])).fetchone()
            if not dup:
                c.execute("insert into mentions (id, response_id, feature_id, kind) values (%s, %s, %s, %s)",
                          ("mnt_" + uuid.uuid4().hex[:12], rid, fid, mn["kind"]))
                count[rid] += 1
        for r in responses:
            note = "held back: still sensitive" if any(h.startswith(r["id"] + "-") for h in held) else None
            done[r["id"]] = {"mentions": count[r["id"]], "note": note}

    sent = (OUT / "backend_sent_to_ai.json").read_text(encoding="utf-8") if answers else ""
    report.update(mentions=sum(count.values()), new_features=new_features,
                  real_values_found_in_sent_request=[v["value"] for v in vault.by_token.values() if found(v["value"], sent)])
    return report


def link_vendors(c: psycopg.Connection) -> int:
    """Every like or dislike links the feature to the vendor that answer was about (no AI: the vendor
    comes from the survey). Runs every pass, so answers read before this rule get their links too."""
    with c.transaction():
        n = c.execute("""insert into vendor_offerings (vendor_id, feature_id)
            select distinct r.vendor_id, m.feature_id from mentions m join responses r on r.id = m.response_id
            where m.kind in ('like', 'dislike') and r.vendor_id is not null
              and not exists (select 1 from vendor_offerings o where o.vendor_id = r.vendor_id and o.feature_id = m.feature_id)""").rowcount
    if n:
        print(f"linked {n} feature(s) to their vendor", flush=True)
    return n


def once(dry: bool) -> list[dict]:
    OUT.mkdir(exist_ok=True)
    url = os.environ.get("DATABASE_URL")
    if not url:
        raise SystemExit("DATABASE_URL is not set")
    with psycopg.connect(url) as c:
        done = load_done()
        todo = pending(c, done)
        by_project: dict[str, list[dict]] = {}
        for r in todo:
            by_project.setdefault(r["project_id"], []).append(r)
        reports = []
        for p, rs in by_project.items():
            reports.append(run_project(c, p, rs, dry, done))
            if not dry:  # saved only after that project's rows committed
                DONE.write_text(json.dumps(done, indent=1), encoding="utf-8")
        if not dry:
            link_vendors(c)
    if reports:  # an empty last pass must not wipe the real report
        (OUT / "backend_run_report.json").write_text(json.dumps(reports, indent=1), encoding="utf-8")
    for rep in reports:
        print(json.dumps({k: v for k, v in rep.items() if k != "ai"}))
    if not reports:
        print("nothing new")
    return reports


def drain(dry: bool) -> None:
    reports = once(dry)
    while reports and not dry:  # 50 responses per AI call; keep going until everything is read
        reports = once(dry)


def watch(every: int = 30) -> None:
    """Never dies on one bad pass (network, AI, database): logs it and tries again next round.
    A failed pass writes nothing and marks nothing done, so those answers are retried."""
    print(f"watching for new survey answers every {every} s", flush=True)
    while True:
        try:
            drain(False)
        except BaseException as e:  # SystemExit from the leak check included: skip this round, keep watching
            if isinstance(e, KeyboardInterrupt):
                raise
            print(f"{time.strftime('%Y-%m-%d %H:%M:%S')} pass failed: {type(e).__name__}: {e}", flush=True)
        sys.stdout.flush()
        time.sleep(every)


if __name__ == "__main__":
    if "--watch" in sys.argv:
        watch()
    else:
        drain("--dry" in sys.argv)
