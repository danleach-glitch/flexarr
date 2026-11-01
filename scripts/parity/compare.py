#!/usr/bin/env python3
"""Side-by-side page of the parity shots: compare.py <before dir> <after dir> <out.html> [notes.json]

Each dir holds <platform>-<nn>-<name>.png. The page has one section per screen, a row per platform, before and after.
notes.json, if given, is {"<platform>-<nn>-<name>": "what changed"}. Images are embedded, so the page stands alone."""
import base64
import html
import json
import re
import sys
from pathlib import Path

PLATFORMS = [("web", "Web"), ("ios", "iPhone"), ("tvos", "Apple TV"), ("android", "Android phone"), ("androidtv", "Android TV")]
SCREENS = [("01-home", "Home"), ("02-library", "A movie library"), ("03-movie", "A movie"), ("04-show", "A show"),
           ("05-player", "Player"), ("06-search", "Search"), ("07-settings", "Settings"), ("08-profiles", "Profiles")]


def img(path):
    if not path.exists():
        return '<div class="missing">No shot</div>'
    data = base64.b64encode(path.read_bytes()).decode()
    return f'<img src="data:image/png;base64,{data}" alt="">'


def main(before, after, out, notes_path=None):
    before, after = Path(before), Path(after)
    notes = json.loads(Path(notes_path).read_text()) if notes_path else {}
    parts = []
    for key, screen in SCREENS:
        rows = []
        for p, name in PLATFORMS:
            stem = f"{p}-{key}"
            b, a = before / f"{stem}.png", after / f"{stem}.png"
            if not b.exists() and not a.exists():
                continue
            note = html.escape(notes.get(stem, ""))
            rows.append(f'<div class="row"><h3>{name}</h3><figure>{img(b)}<figcaption>Before</figcaption></figure>'
                        f'<figure>{img(a)}<figcaption>After</figcaption></figure>'
                        f'{f"<p class=note>{note}</p>" if note else ""}</div>')
        if rows:
            parts.append(f'<section><h2>{screen}</h2>{"".join(rows)}</section>')
    page = f"""<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>Flexarr parity: before and after</title>
<style>
@page {{ size: A4 landscape; margin: 12mm; }}
body {{ font: 14px/1.5 system-ui, sans-serif; color: #1b1720; margin: 24px; }}
h1 {{ font-size: 28px; margin: 0 0 4px; }} h2 {{ font-size: 22px; margin: 32px 0 8px; border-bottom: 1px solid #ccc; break-before: page; }}
section:first-of-type h2 {{ break-before: auto; }}
.row {{ display: grid; grid-template-columns: 9rem 1fr 1fr; gap: 12px; align-items: start; margin: 12px 0; break-inside: avoid; }}
.row h3 {{ margin: 0; font-size: 15px; }}
figure {{ margin: 0; }} figure img {{ width: 100%; border: 1px solid #ddd; border-radius: 4px; }}
figcaption {{ color: #666; font-size: 12px; }}
.note {{ grid-column: 2 / 4; margin: 0; }}
.missing {{ display: grid; place-items: center; height: 120px; background: #f3f1ee; color: #888; border-radius: 4px; }}
</style></head><body><h1>Flexarr parity: before and after</h1>
<p>The same eight screens on each client, from <code>main</code> (before) and <code>ux/parity-audit</code> (after).</p>
{"".join(parts)}</body></html>"""
    Path(out).write_text(page)
    print(f"{out}: {len(parts)} screens")


if __name__ == "__main__":
    main(*sys.argv[1:])
