"""The ICU MessageFormat subset Flexarr's messages use (docs/languages.md): plain text, {arg}, {arg, number},
{arg, plural, ...} with # and =N, {arg, select, ...}, and ICU's apostrophe quoting (DOUBLE_OPTIONAL: '' is an
apostrophe; ' starts quoting only before { } or, inside a plural, #). Every platform's formatter implements exactly
this; languages/tests/format-cases.json keeps them in step with this one, which is the reference."""
import re
from dataclasses import dataclass, field

ARG_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
SELECTOR = re.compile(r"^(=\d+|[A-Za-z_][A-Za-z0-9_-]*)$")
PLURAL_CATEGORIES = {"zero", "one", "two", "few", "many", "other"}


class MessageError(ValueError):
    pass


@dataclass
class Arg:
    name: str
    kind: str = "simple"  # simple | number | plural | select
    options: dict = field(default_factory=dict)  # selector -> list of parts


@dataclass
class Pound:
    pass


def parse(message):
    """A message as a list of parts: str, Arg or Pound. Raises MessageError."""
    parts, pos = _parse(message, 0, False, False)
    if pos != len(message):
        raise MessageError(f"unexpected '}}' at {pos}")
    return parts


def _parse(s, pos, in_plural, nested):
    parts, text = [], []
    n = len(s)
    while pos < n:
        c = s[pos]
        if c == "'":
            if pos + 1 < n and s[pos + 1] == "'":
                text.append("'")
                pos += 2
                continue
            if pos + 1 < n and (s[pos + 1] in "{}" or (in_plural and s[pos + 1] == "#")):
                end = pos + 1
                quoted = []
                while True:
                    if end >= n:
                        raise MessageError("unterminated quote")
                    if s[end] == "'":
                        if end + 1 < n and s[end + 1] == "'":
                            quoted.append("'")
                            end += 2
                            continue
                        break
                    quoted.append(s[end])
                    end += 1
                text.append("".join(quoted))
                pos = end + 1
                continue
            text.append("'")
            pos += 1
        elif c == "{":
            if text:
                parts.append("".join(text))
                text = []
            arg, pos = _parse_arg(s, pos + 1)
            parts.append(arg)
        elif c == "}":
            if not nested:
                raise MessageError(f"unexpected '}}' at {pos}")
            break
        elif c == "#" and in_plural:
            if text:
                parts.append("".join(text))
                text = []
            parts.append(Pound())
            pos += 1
        else:
            text.append(c)
            pos += 1
    if text:
        parts.append("".join(text))
    return parts, pos


def _skip_ws(s, pos):
    while pos < len(s) and s[pos].isspace():
        pos += 1
    return pos


def _word(s, pos):
    start = pos
    while pos < len(s) and (s[pos].isalnum() or s[pos] in "_-="):
        pos += 1
    return s[start:pos], pos


def _parse_arg(s, pos):
    pos = _skip_ws(s, pos)
    name, pos = _word(s, pos)
    if not ARG_NAME.match(name):
        raise MessageError(f"bad value name {name!r}")
    pos = _skip_ws(s, pos)
    if pos < len(s) and s[pos] == "}":
        return Arg(name), pos + 1
    if pos >= len(s) or s[pos] != ",":
        raise MessageError(f"expected ',' or '}}' after {{{name}")
    pos = _skip_ws(s, pos + 1)
    kind, pos = _word(s, pos)
    pos = _skip_ws(s, pos)
    if kind == "number":
        if pos < len(s) and s[pos] == "}":
            return Arg(name, "number"), pos + 1
        raise MessageError(f"{{{name}, number}} takes no style")
    if kind not in ("plural", "select"):
        raise MessageError(f"unsupported type {kind!r} in {{{name}}}")
    if pos >= len(s) or s[pos] != ",":
        raise MessageError(f"expected ',' after {{{name}, {kind}")
    pos += 1
    options = {}
    while True:
        pos = _skip_ws(s, pos)
        if pos < len(s) and s[pos] == "}":
            pos += 1
            break
        sel, pos = _word(s, pos)
        if not SELECTOR.match(sel):
            raise MessageError(f"bad selector {sel!r} in {{{name}}}")
        if kind == "select" and sel.startswith("="):
            raise MessageError(f"select can't use {sel}")
        if sel in options:
            raise MessageError(f"selector {sel!r} twice in {{{name}}}")
        pos = _skip_ws(s, pos)
        if pos >= len(s) or s[pos] != "{":
            raise MessageError(f"expected '{{' after {sel!r} in {{{name}}}")
        sub, pos = _parse(s, pos + 1, kind == "plural", True)
        if pos >= len(s) or s[pos] != "}":
            raise MessageError(f"unclosed option {sel!r} in {{{name}}}")
        options[sel] = sub
        pos += 1
    if "other" not in options:
        raise MessageError(f"{{{name}, {kind}}} needs an 'other'")
    return Arg(name, kind, options), pos


def args(parts, out=None):
    """{name: kind} for every value a message uses (a plural's value counts as 'plural')."""
    out = {} if out is None else out
    for p in parts:
        if isinstance(p, Arg):
            prev = out.get(p.name)
            out[p.name] = p.kind if prev in (None, "simple") else prev
            for sub in p.options.values():
                args(sub, out)
    return out


def selectors(parts, out=None):
    """[(name, kind, set of selectors)] for every plural and select."""
    out = [] if out is None else out
    for p in parts:
        if isinstance(p, Arg) and p.kind in ("plural", "select"):
            out.append((p.name, p.kind, set(p.options)))
            for sub in p.options.values():
                selectors(sub, out)
    return out


def texts(parts):
    """Every piece of plain text, for markup checks."""
    for p in parts:
        if isinstance(p, str):
            yield p
        elif isinstance(p, Arg):
            for sub in p.options.values():
                yield from texts(sub)


def unparse(parts, in_plural=False, transform=None):
    """A message back as text, with each piece of text passed through transform (for the pseudo-pack)."""
    out = []
    for p in parts:
        if isinstance(p, str):
            t = transform(p) if transform else p
            out.append(_quote(t, in_plural))
        elif isinstance(p, Pound):
            out.append("#")
        elif p.kind == "simple":
            out.append("{" + p.name + "}")
        elif p.kind == "number":
            out.append("{" + p.name + ", number}")
        else:
            opts = " ".join(f"{k} {{{unparse(v, p.kind == 'plural', transform)}}}" for k, v in p.options.items())
            out.append(f"{{{p.name}, {p.kind}, {opts}}}")
    return "".join(out)


def _quote(t, in_plural):
    t = t.replace("'", "''")
    special = "{}#" if in_plural else "{}"
    return "".join(f"'{c}'" if c in special else c for c in t)
