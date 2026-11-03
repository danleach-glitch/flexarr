"""Finds every string the code looks up (docs/languages.md, *Writing a string*): t('…'), t.rich('…') and tk('…') on
the web, t("…") and tk("…") in Swift and Kotlin, Loc.T("…"), Loc.Mark("…"), Problems.Of("…") and Problems.Field("…") in C#. A call whose first argument
isn't a string literal is a lookup of something already marked (tk / Loc.Mark), so it's skipped; a literal that
interpolates is an error, because its English can't be known."""
import re
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTEXT = re.compile(r"^[a-z][a-z-]*$")
CONTEXT_KEY = re.compile(r"^[a-z][a-z-]*::")


@dataclass(frozen=True)
class Source:
    scope: str
    root: str
    suffixes: tuple
    call: re.Pattern
    lang: str
    skip: re.Pattern


SKIP_COMMON = r"(/node_modules/|/build/|/\.build/|/DerivedData/|/bin/|/obj/|\.generated\.)"
SOURCES = [
    Source("web", "frontend/src", (".ts", ".tsx"), re.compile(r"(?<![\w.$])(?:t|t\.rich|tk)\(\s*"), "ts",
           re.compile(SKIP_COMMON + r"|\.test\.tsx?$|/test/")),
    Source("apple", "clients/apple", (".swift",), re.compile(r"(?<![\w.])(?:t|tk)\(\s*"), "swift",
           re.compile(SKIP_COMMON + r"|Tests/|UITests/|/Tools/")),
    Source("apple", "clients/macos", (".swift",), re.compile(r"(?<![\w.])(?:t|tk)\(\s*"), "swift",
           re.compile(SKIP_COMMON + r"|Tests/|/Tools/")),
    Source("android", "clients/android", (".kt",), re.compile(r"(?<![\w.])(?:t|tk)\(\s*"), "kotlin",
           re.compile(SKIP_COMMON + r"|/src/test/|/src/androidTest/|/baselineprofile/")),
    Source("server", "src", (".cs",), re.compile(r"\b(?:Loc\.(?:T|Mark)|Problems\.(?:Of|Field))\(\s*"), "cs",
           re.compile(SKIP_COMMON + r"|/Migrations/")),
]

SIMPLE_ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "0": "\0", "\\": "\\", "'": "'", '"': '"', "$": "$", "`": "`"}


class ScanError(ValueError):
    pass


def read_literal(src, pos, lang):
    """(text, end) for the string literal starting at pos, or None when there's no literal there."""
    if lang == "cs" and src.startswith(("$\"", "@\"", "$@\"", "@$\"", "\"\"\""), pos):
        raise ScanError("interpolated, verbatim or raw strings can't be looked up")
    if lang in ("swift", "kotlin") and src.startswith('"""', pos):
        raise ScanError("multi-line strings can't be looked up")
    if lang == "swift" and src.startswith('#"', pos):
        raise ScanError("raw strings can't be looked up")
    quotes = {"ts": "'\"`", "swift": '"', "kotlin": '"', "cs": '"'}[lang]
    if pos >= len(src) or src[pos] not in quotes:
        return None
    q = src[pos]
    out = []
    i = pos + 1
    while i < len(src):
        c = src[i]
        if c == q:
            return "".join(out), i + 1
        if c == "\n" and q != "`":
            raise ScanError("unterminated string")
        if c == "\\":
            nxt = src[i + 1]
            if lang == "swift" and nxt == "(":
                raise ScanError("Swift interpolation \\( ) can't be looked up: pass a value instead")
            if nxt == "u":
                if src[i + 2] == "{":
                    end = src.index("}", i)
                    out.append(chr(int(src[i + 3:end], 16)))
                    i = end + 1
                else:
                    out.append(chr(int(src[i + 2:i + 6], 16)))
                    i += 6
                continue
            if nxt == "x" and lang in ("ts", "cs"):
                out.append(chr(int(src[i + 2:i + 4], 16)))
                i += 4
                continue
            if nxt in SIMPLE_ESCAPES:
                out.append(SIMPLE_ESCAPES[nxt])
                i += 2
                continue
            raise ScanError(f"unknown escape \\{nxt}")
        if q == "`" and src.startswith("${", i):
            raise ScanError("template interpolation ${} can't be looked up: pass a value instead")
        if lang == "kotlin" and c == "$" and i + 1 < len(src) and (src[i + 1] == "{" or src[i + 1].isalpha()):
            raise ScanError("Kotlin templates ($x) can't be looked up: pass a value instead")
        out.append(c)
        i += 1
    raise ScanError("unterminated string")


def call_end(src, pos):
    """The index just past the ')' closing the call whose arguments start at pos (strings and nesting skipped)."""
    depth = 1
    i = pos
    while i < len(src) and depth:
        c = src[i]
        if c in "\"'`":
            q = c
            i += 1
            while i < len(src) and src[i] != q:
                i += 2 if src[i] == "\\" else 1
        elif c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        i += 1
    return i


CTX = re.compile(r"\bctx\s*[:=]\s*[\"']([^\"']*)[\"']")


def scan_text(src, lang, call):
    """[(key, line, error)] for one file's source."""
    found = []
    for m in call.finditer(src):
        line = src.count("\n", 0, m.start()) + 1
        before = src[src.rfind("\n", 0, m.start()) + 1:m.start()]
        if re.search(r"(^|\s)//", before) or before.lstrip().startswith("*"):
            continue  # a comment: an example, not a lookup
        try:
            lit = read_literal(src, m.end(), lang)
        except ScanError as e:
            found.append((None, line, str(e)))
            continue
        if lit is None:
            continue
        text, end = lit
        if not text.strip():
            found.append((None, line, "empty string"))
            continue
        if CONTEXT_KEY.match(text):
            found.append((None, line, f"{text!r} looks like a context key: pass ctx instead"))
            continue
        rest = src[end:call_end(src, end)]
        c = CTX.search(rest)
        if c:
            if not CONTEXT.match(c.group(1)):
                found.append((None, line, f"context {c.group(1)!r} must be lowercase words with hyphens"))
                continue
            found.append((f"{c.group(1)}::{text}", line, None))
        else:
            found.append((text, line, None))
    return found


def scan(root=ROOT, sources=SOURCES):
    """({key: set of scopes}, [error lines])."""
    keys, errors = {}, []
    for s in sources:
        base = root / s.root
        if not base.exists():
            continue
        for path in sorted(base.rglob("*")):
            rel = path.relative_to(root).as_posix()
            if path.suffix not in s.suffixes or not path.is_file() or s.skip.search("/" + rel):
                continue
            for key, line, error in scan_text(path.read_text(encoding="utf-8"), s.lang, s.call):
                if error:
                    errors.append(f"{rel}:{line}: {error}")
                else:
                    keys.setdefault(key, set()).add(s.scope)
    return keys, errors


def english(key):
    """The English a key shows: the key without its context."""
    return CONTEXT_KEY.sub("", key, count=1)
