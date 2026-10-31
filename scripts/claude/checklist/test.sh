#!/bin/sh
# Renders both examples, then checks that broken data is refused. Run after changing anything here.
set -e
cd "$(dirname "$0")"
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
python3 render.py brief example-brief.json "$tmp/brief.html"
python3 render.py audit example-audit.json "$tmp/audit.html"
python3 render.py hub hub.json "$tmp/hub.html"
python3 render.py extract "$tmp/brief.html" "$tmp/back.json"
python3 -c "import json,sys; sys.exit(json.load(open('example-brief.json')) != json.load(open('$tmp/back.json')))" \
  || { echo "extract didn't give back the same data"; exit 1; }

fails() { # name, python that edits the data dict d
  python3 - "$1" "$2" "$3" "$tmp" <<'PY'
import json, sys
kind, name, edit, tmp = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
d = json.load(open(f"example-{kind}.json"))
exec(edit)
json.dump(d, open(f"{tmp}/bad.json", "w"))
PY
  if python3 render.py "$1" "$tmp/bad.json" "$tmp/bad.html" 2>"$tmp/err"; then
    echo "FAIL: $2 was accepted"; exit 1
  fi
  echo "ok: $2 refused ($(head -1 "$tmp/err"))"
}
fails brief "duplicate id"      'd["phases"][0]["items"][1]["id"] = "srv-1"'
fails brief "skipped number"    'd["phases"][0]["items"][0]["id"] = "srv-2"'
fails brief "unknown prefix"    'd["phases"][0]["items"][0]["id"] = "server-1"'
fails brief "missing brief"     'del d["brief"]'
fails audit "unknown must-fix"  'd["mustFix"][0]["ids"] = ["bug-9"]'
fails audit "wrong section"     'd["sections"]["bug"]["items"][1]["id"] = "perf-2"'
fails audit "bad severity"      'd["sections"]["bug"]["items"][0]["sev"] = "Critical"'
echo '{"title": "Checklists"}' > "$tmp/badhub.json"
if python3 render.py hub "$tmp/badhub.json" "$tmp/bad.html" 2>/dev/null; then echo "FAIL: hub without connector was accepted"; exit 1; fi
echo "ok: hub without connector refused"
echo "all checklist checks passed"
