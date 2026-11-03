#!/usr/bin/env python3
"""Wraps hard-coded English in the lookup, for the mechanical part of moving a codebase onto languages/ (tool-12).
Only the safe shapes are changed; everything else it finds is listed for doing by hand (plurals, sentences built from
pieces, interpolation).

    python3 scripts/i18n/wrap.py ts|swift|kotlin <file or folder>... [--dry-run]

ts:     JSX text that is an element's whole content, and aria-label/title/placeholder/alt/label/help/description
        attributes with a plain string.
swift:  Text/Button/Label/Toggle/Section/Picker/TextField/Link/Menu/LabeledContent/navigationTitle/alert/
        confirmationDialog/accessibilityLabel/help with a plain first string.
kotlin: Text("…") and the named arguments text/label/title/contentDescription/placeholder/subtitle/description.

It doesn't add imports: the web needs `import { t } from '<rel>/i18n'`, which it prints for each file it changed.
"""
import html
import re
import sys
from pathlib import Path

ENGLISH = re.compile(r"[A-Za-z]{2}")

TS_TEXT = re.compile(r"(>)([^<>{}`]*?[A-Za-z][^<>{}`]*?)(</)")
TS_ATTR = re.compile(r"\b(aria-label|title|placeholder|alt|label|help|description|ariaLabel)=(\"([^\"{}]*)\"|'([^'{}]*)')")
SWIFT_CALL = re.compile(r"\b(Text|Button|Label|Toggle|Section|Picker|TextField|SecureField|Link|Menu|LabeledContent|"
                        r"navigationTitle|alert|confirmationDialog|accessibilityLabel|help|ContentUnavailableView)"
                        r"\(\s*\"((?:[^\"\\\n]|\\.)*)\"")
KOTLIN_CALL = re.compile(r"\bText\(\s*\"((?:[^\"\\\n$]|\\.)*)\"")
KOTLIN_NAMED = re.compile(r"\b(text|label|title|contentDescription|placeholder|subtitle|description)\s*=\s*"
                          r"\"((?:[^\"\\\n$]|\\.)*)\"")


def ts_quote(s):
    if "'" in s and '"' not in s:
        return '"' + s.replace("\\", "\\\\") + '"'
    return "'" + s.replace("\\", "\\\\").replace("'", "\\'") + "'"


def wrap_ts(src):
    manual = []

    def text(m):
        raw = m.group(2)
        if not ENGLISH.search(raw) or "=>" in raw or "&&" in raw or "||" in raw:
            return m.group(0)
        lead = raw[: len(raw) - len(raw.lstrip())]
        trail = raw[len(raw.rstrip()):]
        body = " ".join(html.unescape(raw.strip()).split())
        if "{" in body or "}" in body:
            return m.group(0)
        return f"{m.group(1)}{lead}{{t({ts_quote(body)})}}{trail}{m.group(3)}"

    out = TS_TEXT.sub(text, src)

    def attr(m):
        value = m.group(3) if m.group(3) is not None else m.group(4)
        if not ENGLISH.search(value):
            return m.group(0)
        return f"{m.group(1)}={{t({ts_quote(html.unescape(value))})}}"

    out = TS_ATTR.sub(attr, out)
    for i, line in enumerate(out.splitlines(), 1):
        if re.search(r">[^<>{}]*[A-Za-z]{2}[^<>{}]*\{(?!t[.(])", line) or re.search(r"\}[^<>{}]*[A-Za-z]{2}[^<>{}]*<", line):
            manual.append((i, "JSX text mixed with values: make it one t() with {values} or t.rich"))
        if re.search(r"=== 1 \?", line):
            manual.append((i, "a plural by hand: use {n, plural, one {…} other {…}}"))
    return out, manual


def wrap_swift(src):
    manual = []

    def call(m):
        s = m.group(2)
        if "\\(" in s:
            line = src.count("\n", 0, m.start()) + 1
            manual.append((line, f"{m.group(1)}(\"…\\(…)…\"): use t(\"… {{value}} …\", [\"value\": …])"))
            return m.group(0)
        if not ENGLISH.search(s):
            return m.group(0)
        return m.group(0).replace(f'"{s}"', f't("{s}")', 1)

    return SWIFT_CALL.sub(call, src), manual


def wrap_kotlin(src):
    manual = []

    def wrap(m, s):
        if not ENGLISH.search(s):
            return m.group(0)
        return m.group(0).replace(f'"{s}"', f't("{s}")', 1)

    out = KOTLIN_CALL.sub(lambda m: wrap(m, m.group(1)), src)
    out = KOTLIN_NAMED.sub(lambda m: wrap(m, m.group(2)), out)
    for i, line in enumerate(out.splitlines(), 1):
        if re.search(r"\b(Text\(|text\s*=|label\s*=|title\s*=)\s*\"[^\"]*\$", line):
            manual.append((i, "a template string: use t(\"… {value} …\", \"value\" to …)"))
    return out, manual


WRAPPERS = {"ts": (wrap_ts, (".tsx", ".ts")), "swift": (wrap_swift, (".swift",)), "kotlin": (wrap_kotlin, (".kt",))}


def main(argv):
    if len(argv) < 2 or argv[0] not in WRAPPERS:
        print(__doc__, file=sys.stderr)
        return 2
    dry = "--dry-run" in argv
    fn, suffixes = WRAPPERS[argv[0]]
    files = []
    for a in argv[1:]:
        if a.startswith("--"):
            continue
        p = Path(a)
        files += sorted(q for q in p.rglob("*") if q.suffix in suffixes) if p.is_dir() else [p]
    changed = 0
    for f in files:
        if re.search(r"\.(test|generated)\.|Tests/|/test/", f.as_posix()):
            continue
        src = f.read_text(encoding="utf-8")
        out, manual = fn(src)
        for line, why in manual:
            print(f"{f}:{line}: by hand: {why}")
        if out != src:
            changed += 1
            if not dry:
                f.write_text(out, encoding="utf-8")
            print(f"{f}: wrapped {out.count('t(') - src.count('t(')}")
    print(f"{changed} files {'would change' if dry else 'changed'}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
