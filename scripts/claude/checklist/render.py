#!/usr/bin/env python3
"""Render a /brief or /audit checklist page from a JSON data file.

    python3 scripts/claude/checklist/render.py brief data.json out.html
    python3 scripts/claude/checklist/render.py audit data.json out.html
    python3 scripts/claude/checklist/render.py hub hub.json out.html
    python3 scripts/claude/checklist/render.py extract page.html data.json

extract pulls the data back out of a published page (save it with read_artifact first), so a later session can
change a page without the scratchpad it was first built in.

The template (brief.html or audit.html), base.css and common.js are fixed: a page only differs by its data.
The data is checked first (required fields, id prefixes, unique ids numbered 1..n per prefix, fix-before-landing
ids that exist), and nothing is written if it fails.
"""
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

BRIEF_PREFIXES = ["srv", "api", "web", "ios", "tv", "and", "atv", "mac", "site", "relay", "ha", "tool", "docs", "test", "fix"]
AUDIT_SECTIONS = ["bug", "perf", "sec", "compat", "ux", "land", "idea"]
SEVERITIES = ["High", "Medium", "Low", "Info"]
SIZES = ["S", "M", "L"]
MARKS = ["✅", "🟡", "❌", "—"]


def fail(errors):
    for e in errors:
        print("checklist: " + e, file=sys.stderr)
    sys.exit(1)


def need(obj, keys, where, errors):
    for k in keys:
        if obj.get(k) in (None, "", []):
            errors.append(f"{where}: missing {k}")


def check_ids(ids, allowed, errors):
    seen = {}
    for i in ids:
        m = re.fullmatch(r"([a-z]+)-(\d+)", i or "")
        if not m:
            errors.append(f"id {i!r} isn't <prefix>-<number>")
            continue
        prefix, n = m.group(1), int(m.group(2))
        if prefix not in allowed:
            errors.append(f"id {i}: prefix {prefix!r} isn't one of {', '.join(allowed)}")
        if n in seen.setdefault(prefix, set()):
            errors.append(f"id {i} is used twice")
        seen[prefix].add(n)
    for prefix, nums in seen.items():
        missing = sorted(set(range(1, max(nums) + 1)) - nums)
        if missing:
            errors.append(f"{prefix}- ids skip {', '.join(f'{prefix}-{n}' for n in missing)} (drop items with a note, never delete them)")


def check_parity(data, errors):
    p = data.get("parity")
    if not p:
        return
    for r in p.get("rows", []):
        need(r, ["mark", "client", "what"], "parity row", errors)
        if r.get("mark") not in MARKS:
            errors.append(f"parity row {r.get('client')!r}: mark must be one of {' '.join(MARKS)}")


def check_brief(data, errors):
    need(data, ["title", "brief", "summary", "date", "kind", "decided", "phases"], "brief", errors)
    if data.get("title") and not data["title"].endswith(" Brief"):
        errors.append("title should end with ' Brief'")
    ids = []
    for p in data.get("phases", []):
        need(p, ["name"], "phase", errors)
        groups = p.get("groups") or [{"name": "", "items": p.get("items", [])}]
        if not any(g.get("items") for g in groups):
            errors.append(f"phase {p.get('name')!r} has no items")
        for item in [i for g in groups for i in g.get("items", [])]:
            need(item, ["id", "title", "check"], f"item {item.get('id')}", errors)
            ids.append(item.get("id"))
    check_ids(ids, BRIEF_PREFIXES, errors)
    check_parity(data, errors)


def check_audit(data, errors):
    need(data, ["title", "branch", "commit", "date", "summary", "sections"], "audit", errors)
    if data.get("title") and not data["title"].endswith(" Audit"):
        errors.append("title should end with ' Audit'")
    sections = data.get("sections", {})
    ids = []
    for key, sec in sections.items():
        if key not in AUDIT_SECTIONS:
            errors.append(f"section {key!r} isn't one of {', '.join(AUDIT_SECTIONS)}")
            continue
        for f in sec.get("items", []):
            need(f, ["id", "sev", "title"], f"finding {f.get('id')}", errors)
            if not str(f.get("id", "")).startswith(key + "-"):
                errors.append(f"finding {f.get('id')} is in the {key} section")
            allowed = SIZES if key == "idea" else SEVERITIES
            if f.get("sev") not in allowed:
                errors.append(f"finding {f.get('id')}: sev must be one of {', '.join(allowed)}")
            ids.append(f.get("id"))
    check_ids(ids, AUDIT_SECTIONS, errors)
    known = set(ids)
    for m in data.get("mustFix", []):
        need(m, ["ids", "title"], "fix-before-landing entry", errors)
        for i in m.get("ids", []):
            if i not in known:
                errors.append(f"fix-before-landing points at {i}, which isn't a finding")
    check_parity(data, errors)


def extract(src, out):
    try:
        page = src.read_text()
    except OSError as e:
        fail([f"can't read {src}: {e}"])
    m = re.search(r'<script id="data" type="application/json">(.*?)</script>', page, re.S)
    if not m:
        fail([f"{src} has no data block; was it rendered from this template?"])
    data = json.loads(m.group(1))  # JSON reads <\/ as </
    out.write_text(json.dumps(data, ensure_ascii=False, indent=1) + "\n")
    print(f"{out}: data from {src}")


def check_hub(data, errors):
    need(data, ["title", "connector"], "hub", errors)
    if not isinstance(data.get("staleDays", 3), int) or data.get("staleDays", 3) < 1:
        errors.append("staleDays must be a whole number of days")


def main():
    if len(sys.argv) == 4 and sys.argv[1] == "extract":
        extract(Path(sys.argv[2]), Path(sys.argv[3]))
        return
    if len(sys.argv) != 4 or sys.argv[1] not in ("brief", "audit", "hub"):
        print(__doc__.strip(), file=sys.stderr)
        sys.exit(2)
    kind, src, out = sys.argv[1], Path(sys.argv[2]), Path(sys.argv[3])
    try:
        data = json.loads(src.read_text())
    except (OSError, json.JSONDecodeError) as e:
        fail([f"can't read {src}: {e}"])
    errors = []
    {"brief": check_brief, "audit": check_audit, "hub": check_hub}[kind](data, errors)
    if errors:
        fail(errors)

    page = (HERE / f"{kind}.html").read_text()
    blob = json.dumps(data, ensure_ascii=False, indent=1).replace("</", "<\\/")
    title = data["title"].replace("&", "&amp;").replace("<", "&lt;")
    for marker, value in (("/*CSS*/", (HERE / "base.css").read_text()),
                          ("/*COMMON_JS*/", (HERE / "common.js").read_text()),
                          ("{{TITLE}}", title),
                          ("/*DATA*/", blob)):
        if marker not in page:
            fail([f"{kind}.html has no {marker} marker"])
        page = page.replace(marker, value, 1)
    out.write_text(page)
    count = len(re.findall(r'"id":', blob))
    print(f"{out}: {kind} page, {count} items, {len(page) // 1024} KB")


if __name__ == "__main__":
    main()
