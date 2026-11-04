#!/usr/bin/env python3
"""Wraps hard-coded English in the lookup, for the mechanical part of moving a codebase onto languages/ (tool-12).
Only the safe shapes are changed; everything else it finds is listed for doing by hand (plurals, sentences built from
pieces, interpolation).

    python3 scripts/i18n/wrap.py cs|ts|swift|kotlin <file or folder>... [--dry-run]

cs:     Results.Problem("…"/$"…", statusCode: …) as Problems.Of, and one-message ValidationProblems as
        Problems.Field, naming each interpolated value after its variable or property.

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


def add_ts_import(src, path):
    """Adds t to the file's import from the i18n module (or a new import after the last one)."""
    import os
    existing = re.search(r"import \{([^}]*)\} from '((?:\.\.?/)+i18n|\.)'", src)
    if existing:
        names = [n.strip() for n in existing.group(1).split(",") if n.strip()]
        if "t" in names:
            return src
        names = sorted(set(names + ["t"]), key=str.lower)
        return src[:existing.start(1)] + " " + ", ".join(names) + " " + src[existing.end(1):]
    rel = os.path.relpath(Path("frontend/src/i18n").resolve(), path.resolve().parent)
    rel = rel if rel.startswith(".") else "./" + rel
    lines = src.split("\n")
    last = -1
    i = 0
    while i < len(lines):
        if lines[i].startswith("import "):
            j = i
            while not re.search(r"from '[^']+'\s*$|^import '[^']+'\s*$", lines[j]):
                j += 1
            last = j
            i = j + 1
            continue
        if lines[i].strip() and not lines[i].startswith("//") and last >= 0:
            break
        i += 1
    lines.insert(last + 1, f"import {{ t }} from '{rel}'")
    return "\n".join(lines)


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


# ---- C# -----------------------------------------------------------------------------------------------------------

CS_PROBLEM = re.compile(r"\bResults\.Problem\(\s*(?=\$?\")")
CS_FIELD = re.compile(r"\bResults\.ValidationProblem\(\s*new Dictionary<string, string\[\]>\s*\{\s*\[\"(\w+)\"\]\s*=\s*\[\s*(?=\$?\")")
CS_STATUS = re.compile(r"\s*(?:,\s*statusCode:\s*(StatusCodes\.\w+|\d{3}))?\s*\)")


def cs_literal(src, pos):
    """(segments, end) for the C# string literal at pos: segments are ('text', str) and ('hole', expr). None when
    it's a shape this doesn't handle (verbatim, raw, format specifiers)."""
    interp = src[pos] == "$"
    if interp:
        pos += 1
    if src[pos] != '"' or src.startswith('"""', pos):
        return None
    i, text, segs = pos + 1, [], []
    esc = {"n": "\n", "t": "\t", '"': '"', "\\": "\\", "'": "'", "0": "\0"}
    while i < len(src):
        c = src[i]
        if c == '"':
            if text:
                segs.append(("text", "".join(text)))
            return segs, i + 1
        if c == "\\":
            nxt = src[i + 1]
            if nxt == "u":
                text.append(chr(int(src[i + 2:i + 6], 16)))
                i += 6
                continue
            if nxt not in esc:
                return None
            text.append(esc[nxt])
            i += 2
            continue
        if interp and c == "{":
            if src.startswith("{{", i):
                text.append("{")
                i += 2
                continue
            depth, j = 1, i + 1
            while j < len(src) and depth:
                if src[j] == "{":
                    depth += 1
                elif src[j] == "}":
                    depth -= 1
                elif src[j] == '"':
                    return None
                j += 1
            if text:
                segs.append(("text", "".join(text)))
                text = []
            segs.append(("hole", src[i + 1:j - 1].strip()))
            i = j
            continue
        if interp and src.startswith("}}", i):
            text.append("}")
            i += 2
            continue
        if c == "\n":
            return None
        text.append(c)
        i += 1
    return None


def cs_name(expr):
    if re.fullmatch(r"[A-Za-z_]\w*", expr):
        return expr[0].lower() + expr[1:]
    if re.fullmatch(r"[A-Za-z_][\w.]*\w", expr):
        segs = expr.split(".")
        last = segs[-1]
        if last in ("Count", "Length") and len(segs) > 1:
            last = segs[-2] + "Count"
        return last[0].lower() + last[1:]
    return None


def cs_icu(segs):
    """(ICU English, [(name, expr)]) or None when a hole can't be named."""
    out, values = [], []
    for idx, (kind, value) in enumerate(segs):
        if kind == "text":
            before_hole = idx + 1 < len(segs) and segs[idx + 1][0] == "hole"
            t = []
            for k, ch in enumerate(value):
                nxt = value[k + 1] if k + 1 < len(value) else ("{" if before_hole else "")
                if ch in "{}":
                    t.append(f"'{ch}'")
                elif ch == "'" and nxt in ("{", "}"):
                    t.append("''")
                else:
                    t.append(ch)
            out.append("".join(t))
        else:
            name = cs_name(value)
            if name is None:
                return None
            known = dict(values)
            if name in known and known[name] != value:
                return None
            if name not in known:
                values.append((name, value))
            out.append("{" + name + "}")
    return "".join(out), values


def cs_quote(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n") + '"'


def cs_args(values):
    if not values:
        return ""
    parts = [n if n == e else f"{n} = {e}" for n, e in values]
    return ", new { " + ", ".join(parts) + " }"


def wrap_cs(src):
    import icu
    manual, out, pos = [], [], 0
    for m in sorted(list(CS_PROBLEM.finditer(src)) + list(CS_FIELD.finditer(src)), key=lambda m: m.start()):
        if m.start() < pos:
            continue
        line = src.count("\n", 0, m.start()) + 1
        lit = cs_literal(src, m.end())
        conv = cs_icu(lit[0]) if lit else None
        if conv:
            try:
                icu.parse(conv[0])
            except icu.MessageError:
                conv = None
        if not conv:
            manual.append((line, "a message this can't convert: use Problems.Of / Problems.Field by hand"))
            continue
        english, values = conv
        end = lit[1]
        if m.re is CS_FIELD:
            tail = re.match(r"\s*\]\s*\}\s*\)", src[end:])
            if not tail:
                manual.append((line, "a validation answer with more than one message"))
                continue
            args = cs_args(values).replace(", new", ", args: new") if values else ""
            new = f'Problems.Field({cs_quote(english)}, field: "{m.group(1)}"{args})'
        else:
            tail = CS_STATUS.match(src[end:])
            if not tail:
                manual.append((line, "Results.Problem with other arguments"))
                continue
            status = tail.group(1) or "500"
            new = f"Problems.Of({cs_quote(english)}{cs_args(values)}, status: {status})"
        out.append(src[pos:m.start()])
        out.append(new)
        pos = end + tail.end()
    out.append(src[pos:])
    result = "".join(out)
    if result != src and "using Flexarr.Api.Localization;" not in result and "namespace Flexarr.Api.Localization" not in result:
        result = re.sub(r"^(namespace )", "using Flexarr.Api.Localization;\n\n\\1", result, count=1, flags=re.M) \
            if not re.search(r"^using ", result, re.M) else re.sub(r"^(using [^\n]+\n)(?!using )", "\\1using Flexarr.Api.Localization;\n", result, count=1, flags=re.M)
    return result, manual


WRAPPERS = {"cs": (wrap_cs, (".cs",)), "ts": (wrap_ts, (".tsx", ".ts")), "swift": (wrap_swift, (".swift",)), "kotlin": (wrap_kotlin, (".kt",))}


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
        if re.search(r"\.(test|generated)\.|Tests/|/test/|/Migrations/", f.as_posix()):
            continue
        src = f.read_text(encoding="utf-8")
        out, manual = fn(src)
        if argv[0] == "ts" and out != src:
            out = add_ts_import(out, f)
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
