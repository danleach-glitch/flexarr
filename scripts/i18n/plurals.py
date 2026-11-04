"""CLDR's cardinal plural rules (languages/cldr/plurals.json) as code for TypeScript, Swift, Kotlin and C#, and as
Python for the checks. Every platform computes the same operands from a number (n, i, v, w, f, t, e, as Doubles):
see `operands` below, which the generated code mirrors."""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "languages" / "cldr" / "plurals.json"
ORDER = ["zero", "one", "two", "few", "many", "other"]
RELATION = re.compile(r"^([nivwfte])\s*(?:%\s*(\d+))?\s*(!=|=)\s*([0-9.,\s]+)$")


def load(path=SOURCE):
    """{locale: {category: [[relation, ...] (and), ...] (or)}}, relations as (operand, mod, negate, [(lo, hi)])."""
    data = json.loads(Path(path).read_text())["cardinal"]
    return {loc: {cat: parse_rule(rule) for cat, rule in cats.items()} for loc, cats in data.items()}


def parse_rule(rule):
    cond = rule.split("@")[0].strip()
    if not cond:
        return []
    out = []
    for alt in cond.split(" or "):
        rels = []
        for rel in alt.split(" and "):
            m = RELATION.match(rel.strip())
            if not m:
                raise ValueError(f"can't read plural relation {rel!r}")
            operand, mod, op, values = m.groups()
            ranges = []
            for item in values.split(","):
                item = item.strip()
                lo, _, hi = item.partition("..")
                ranges.append((int(lo), int(hi or lo)))
            rels.append((operand, int(mod) if mod else None, op == "!=", ranges))
        out.append(rels)
    return out


def categories(rules, locale):
    """The categories a locale has (always including other)."""
    r = rules.get(resolve(rules, locale), rules["und"])
    return {c for c in r} | {"other"}


def resolve(rules, locale):
    """The CLDR locale a BCP 47 code uses: pt-PT, then pt, then und."""
    code = locale.replace("_", "-")
    while code:
        if code in rules:
            return code
        if "-" not in code:
            break
        code = code.rsplit("-", 1)[0]
    return "und"


def operands(x):
    """CLDR's operands of a number, the way every platform computes them: from its shortest decimal form, with a
    whole number having no fraction digits."""
    n = abs(float(x))
    s = repr(n)
    if "e" in s or "E" in s or n == int(n):
        return {"n": n, "i": float(int(n)), "v": 0.0, "w": 0.0, "f": 0.0, "t": 0.0, "e": 0.0}
    whole, frac = s.split(".")
    stripped = frac.rstrip("0")
    return {"n": n, "i": float(int(whole)), "v": float(len(frac)), "w": float(len(stripped)), "f": float(int(frac)),
            "t": float(int(stripped or "0")), "e": 0.0}


def category(rules, locale, x):
    o = operands(x)
    for cat in ORDER:
        alts = rules.get(resolve(rules, locale), rules["und"]).get(cat)
        if cat == "other" or alts is None:
            continue
        if any(all(_holds(o, rel) for rel in alt) for alt in alts):
            return cat
    return "other"


def _holds(o, rel):
    operand, mod, negate, ranges = rel
    value = o[operand] % mod if mod else o[operand]
    whole = value == int(value)
    hit = any((lo <= value <= hi) and (whole or lo == hi and value == lo) for lo, hi in ranges)
    return hit != negate


# ---- code generation ---------------------------------------------------------------------------------------------

def _groups(rules):
    """Locales sharing identical rules, so each function is written once: [(signature, rules, [locales])]."""
    seen = {}
    for loc in sorted(rules):
        sig = json.dumps({c: rules[loc][c] for c in ORDER if c in rules[loc]})
        seen.setdefault(sig, ([], rules[loc]))[0].append(loc)
    return [(locs, r) for locs, r in seen.values()]


def _expr(rel, lang):
    operand, mod, negate, ranges = rel
    o = f"o.{operand.upper()}" if lang == "cs" else f"o.{operand}"
    if mod:
        o = {"ts": f"md({o}, {mod})", "swift": f"md({o}, {mod})", "kotlin": f"md({o}, {mod})", "cs": f"Md({o}, {mod})"}[lang]
    rng = "Rng" if lang == "cs" else "rng"
    eq = "===" if lang == "ts" else "=="
    lit = (lambda v: f"{v}") if lang == "ts" else (lambda v: f"{v}.0")
    items = [f"{o} {eq} {lit(lo)}" if lo == hi else f"{rng}({o}, {lit(lo)}, {lit(hi)})" for lo, hi in ranges]
    joined = " || ".join(items)
    if len(items) > 1:
        joined = f"({joined})"
    return f"!{joined}" if negate and len(items) > 1 else (f"!({joined})" if negate else joined)


def _cond(alts, lang):
    and_, or_ = "&&", "||"
    parts = []
    for alt in alts:
        e = f" {and_} ".join(_expr(r, lang) for r in alt)
        parts.append(f"({e})" if len(alt) > 1 and len(alts) > 1 else e)
    return f" {or_} ".join(parts)


HEADER = "Generated from languages/cldr/plurals.json by `make strings`. Don't edit."


def typescript(rules):
    groups = _groups(rules)
    lines = [f"// {HEADER}", "", "export type PluralCategory = 'zero' | 'one' | 'two' | 'few' | 'many' | 'other'",
             "export interface Operands { n: number; i: number; v: number; w: number; f: number; t: number; e: number }",
             "type Rule = (o: Operands) => PluralCategory",
             "const md = (x: number, m: number) => x % m",
             "const rng = (x: number, lo: number, hi: number) => Number.isInteger(x) && x >= lo && x <= hi", ""]
    names = {}
    for idx, (locs, r) in enumerate(groups):
        body = [f"  if ({_cond(r[c], 'ts')}) return '{c}'" for c in ORDER if c != "other" and r.get(c)]
        if body:
            lines += [f"const r{idx}: Rule = (o) => {{", *body, "  return 'other'", "}"]
        else:
            lines += [f"const r{idx}: Rule = () => 'other'"]
        for loc in locs:
            names[loc] = f"r{idx}"
    lines += ["", "export const pluralRules: Record<string, Rule> = {"]
    lines += [f"  '{loc}': {names[loc]}," for loc in sorted(names)]
    lines += ["}", ""]
    return "\n".join(lines)


def swift(rules):
    groups = _groups(rules)
    lines = [f"// {HEADER}", "", "// swiftlint:disable all", "", "enum PluralRules {",
             "    private static func md(_ x: Double, _ m: Double) -> Double { x.truncatingRemainder(dividingBy: m) }",
             "    private static func rng(_ x: Double, _ lo: Double, _ hi: Double) -> Bool {",
             "        x.rounded(.towardZero) == x && x >= lo && x <= hi",
             "    }", ""]
    names = {}
    for idx, (locs, r) in enumerate(groups):
        lines.append(f"    static func r{idx}(_ o: PluralOperands) -> PluralCategory {{")
        for c in ORDER:
            if c != "other" and r.get(c):
                lines.append(f"        if {_cond(r[c], 'swift')} {{ return .{c} }}")
        if not any(c != "other" and r.get(c) for c in ORDER):
            lines.append("        _ = o")
        lines += ["        return .other", "    }"]
        for loc in locs:
            names[loc] = f"r{idx}"
    lines += ["", "    static let byLocale: [String: @Sendable (PluralOperands) -> PluralCategory] = ["]
    lines += [f"        \"{loc}\": {names[loc]}," for loc in sorted(names)]
    lines += ["    ]", "}", ""]
    return "\n".join(lines)


def kotlin(rules, package):
    groups = _groups(rules)
    lines = [f"// {HEADER}", "", f"package {package}", "", "@Suppress(\"ktlint\", \"CyclomaticComplexMethod\")",
             "internal object PluralRules {",
             "    private fun md(x: Double, m: Int): Double = x % m",
             "    private fun rng(x: Double, lo: Double, hi: Double): Boolean = x % 1.0 == 0.0 && x >= lo && x <= hi", ""]
    names = {}
    for idx, (locs, r) in enumerate(groups):
        lines.append(f"    private fun r{idx}(o: PluralOperands): PluralCategory {{")
        for c in ORDER:
            if c != "other" and r.get(c):
                lines.append(f"        if ({_cond(r[c], 'kotlin')}) return PluralCategory.{c.upper()}")
        lines += ["        return PluralCategory.OTHER", "    }"]
        for loc in locs:
            names[loc] = f"r{idx}"
    lines += ["", "    val byLocale: Map<String, (PluralOperands) -> PluralCategory> = mapOf("]
    lines += [f"        \"{loc}\" to ::{names[loc]}," for loc in sorted(names)]
    lines += ["    )", "}", ""]
    return "\n".join(lines)


def csharp(rules, namespace):
    groups = _groups(rules)
    lines = [f"// {HEADER}", "", "// <auto-generated />", "#pragma warning disable", "", f"namespace {namespace};", "",
             "internal static class PluralRules", "{",
             "    private static double Md(double x, int m) => x % m;", "",
             "    private static bool Rng(double x, double lo, double hi) => x % 1 == 0 && x >= lo && x <= hi;", ""]
    names = {}
    for idx, (locs, r) in enumerate(groups):
        lines += [f"    private static PluralCategory R{idx}(PluralOperands o)", "    {"]
        for c in ORDER:
            if c != "other" and r.get(c):
                lines += [f"        if ({_cond(r[c], 'cs')})", "        {", f"            return PluralCategory.{c.capitalize()};", "        }", ""]
        lines += ["        return PluralCategory.Other;", "    }", ""]
        for loc in locs:
            names[loc] = f"R{idx}"
    lines += ["    public static readonly IReadOnlyDictionary<string, Func<PluralOperands, PluralCategory>> ByLocale =",
              "        new Dictionary<string, Func<PluralOperands, PluralCategory>>(StringComparer.Ordinal)", "        {"]
    lines += [f"            [\"{loc}\"] = {names[loc]}," for loc in sorted(names)]
    lines += ["        };", "}", ""]
    return "\n".join(lines)
