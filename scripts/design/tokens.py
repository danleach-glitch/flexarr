#!/usr/bin/env python3
"""Write design/tokens.json (and the score marks in design/scores.json) out as CSS, TypeScript, Swift and Kotlin
(`make tokens`). `--check` fails when a file is stale."""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "design" / "tokens.json"
CSS = ROOT / "frontend/src/styles/tokens.generated.css"
SWIFT = ROOT / "clients/apple/Shared/Design/Tokens.swift"
KOTLIN = ROOT / "clients/android/app/src/main/kotlin/io/github/flexarr/ui/theme/Tokens.kt"
WEB_ICONS = ROOT / "frontend/src/components/icons.generated.ts"
WEB_ICON_SOURCE = ROOT / "frontend/src/components/icons.tsx"
SWIFT_ICONS = ROOT / "clients/apple/Shared/Design/Icons.swift"
KOTLIN_ICONS = ROOT / "clients/android/app/src/main/kotlin/io/github/flexarr/ui/theme/FlexarrIcons.kt"
SCORES = ROOT / "design" / "scores.json"
WEB_SCORES = ROOT / "frontend/src/components/scoreMarks.generated.ts"
SWIFT_SCORES = ROOT / "clients/apple/Shared/Design/ScoreMarks.swift"
KOTLIN_SCORES = ROOT / "clients/android/app/src/main/kotlin/io/github/flexarr/ui/theme/ScoreMarks.kt"
SCORES_HEADER = "Generated from design/scores.json by `make tokens`. Don't edit; redraw with scripts/design/draw_scores.py."
HEADER = "Generated from design/tokens.json by `make tokens`. Don't edit; change the JSON."

HEX = re.compile(r"^#[0-9A-Fa-f]{6}$")
NAME = re.compile(r"^[a-z][A-Za-z]*$")
REQUIRED_COLORS = {"room", "seat", "aisle", "screen", "usher", "silver", "go", "danger", "record", "well"}
REQUIRED_RADII = {"art", "control", "panel"}
MATERIAL = re.compile(r"^(AutoMirrored\.)?(Filled|Outlined|Rounded|Sharp|TwoTone)\.[A-Z][A-Za-z0-9]*$")
SF = re.compile(r"^[a-z0-9]+(\.[a-z0-9]+)*$")


def icons(data):
    """The icon entries, without the "about" note."""
    return {k: v for k, v in data.get("icons", {}).items() if k != "about"}


def web_icon_names():
    return set(re.findall(r"^export const ([A-Z][A-Za-z0-9]*) =", WEB_ICON_SOURCE.read_text(), re.M))


def load(path=SOURCE):
    data = json.loads(Path(path).read_text())
    validate(data)
    return data


def validate(data):
    errors = []
    colors, radii = data.get("colors", {}), data.get("radii", {})
    for name in REQUIRED_COLORS - colors.keys():
        errors.append(f"missing colour {name}")
    for name in REQUIRED_RADII - radii.keys():
        errors.append(f"missing radius {name}")
    for name, c in colors.items():
        if not NAME.match(name):
            errors.append(f"colour name {name!r} isn't camelCase")
        if not HEX.match(c.get("hex", "")):
            errors.append(f"colour {name}: {c.get('hex')!r} isn't #RRGGBB")
    for name, r in radii.items():
        if not isinstance(r.get("value"), (int, float)) or r["value"] < 0:
            errors.append(f"radius {name}: needs a number")
    known = web_icon_names() if WEB_ICON_SOURCE.exists() else None
    for name, i in icons(data).items():
        if not NAME.match(name):
            errors.append(f"icon name {name!r} isn't camelCase")
        for key in ("web", "sf", "material", "use"):
            if not i.get(key):
                errors.append(f"icon {name}: missing {key}")
        if known is not None and i.get("web") and i["web"] not in known:
            errors.append(f"icon {name}: the web has no icon {i['web']!r} in icons.tsx")
        if i.get("material") and not MATERIAL.match(i["material"]):
            errors.append(f"icon {name}: {i['material']!r} isn't Style.Name, e.g. Filled.PlayArrow")
        if i.get("sf") and not SF.match(i["sf"]):
            errors.append(f"icon {name}: {i['sf']!r} isn't an SF Symbol name")
    mix = data.get("mainButton", {}).get("ambientMix")
    if not isinstance(mix, (int, float)) or not 0 <= mix <= 1:
        errors.append("mainButton.ambientMix must be between 0 and 1")
    if errors:
        raise ValueError("; ".join(errors))


def kebab(name):
    return re.sub(r"([A-Z])", lambda m: "-" + m.group(1).lower(), name)


def num(x):
    return f"{x:g}"


def css(data):
    lines = [f"/* {HEADER} */", ":root {"]
    for name, c in data["colors"].items():
        lines.append(f"  /* {c['use']} */")
        lines.append(f"  --{kebab(name)}: {c['hex'].lower()};")
    for name, r in data["radii"].items():
        lines.append(f"  --radius-{kebab(name)}: {num(r['value'])}px;")
    lines.append(f"  --main-button-mix: {round(data['mainButton']['ambientMix'] * 100):g}%;")
    t = data["typeScale"]
    for s in t["steps"]:
        lines.append(f"  --step-{s}: {round(t['ratio'] ** s, 4):g}rem;")
    lines.append("}")
    return "\n".join(lines) + "\n"


def swift(data):
    lines = [f"// {HEADER}", "", "import SwiftUI", "", "extension Color {"]
    for name, c in data["colors"].items():
        h = c["hex"].lstrip("#")
        r, g, b = (int(h[i:i + 2], 16) for i in (0, 2, 4))
        lines.append(f"    /// {c['use']}.")
        lines.append(f"    static let {name} = Color(.sRGB, red: {r} / 255, green: {g} / 255, blue: {b} / 255)")
    lines += ["}", "", "/// Corner radii, in points.", "enum Radius {"]
    for name, r in data["radii"].items():
        lines.append(f"    /// {r['use']}.")
        lines.append(f"    static let {name}: CGFloat = {num(r['value'])}")
    lines += ["}", "", "enum MainButton {", f"    /// {data['mainButton']['use']}.",
              f"    static let ambientMix: Double = {num(data['mainButton']['ambientMix'])}", "}"]
    return "\n".join(lines) + "\n"


def kotlin(data):
    lines = [f"// {HEADER}", "", "package io.github.flexarr.ui.theme", "",
             "import androidx.compose.ui.graphics.Color", "import androidx.compose.ui.unit.dp", "",
             "object Tokens {"]
    for name, c in data["colors"].items():
        lines.append(f"    /** {c['use']}. */")
        lines.append(f"    val {name} = Color(0xFF{c['hex'].lstrip('#').upper()})")
    for name, r in data["radii"].items():
        cap = name[0].upper() + name[1:]
        lines.append(f"    /** {r['use']}. */")
        lines.append(f"    val radius{cap} = {num(r['value'])}.dp")
    lines += [f"    /** {data['mainButton']['use']}. */",
              f"    const val MAIN_BUTTON_AMBIENT_MIX = {num(data['mainButton']['ambientMix'])}f", "}"]
    return "\n".join(lines) + "\n"


def web_icons(data):
    entries = icons(data)
    names = sorted({i["web"] for i in entries.values()})
    lines = [f"// {HEADER}", "", f"import {{ {', '.join(names)} }} from './icons'", "",
             "/** The icon for each action and place, the same idea on every client (design/tokens.json › icons). */",
             "export const icon = {"]
    for name, i in entries.items():
        lines.append(f"  /** {i['use']} */")
        lines.append(f"  {name}: {i['web']},")
    lines.append("} as const")
    return "\n".join(lines) + "\n"


def swift_icons(data):
    entries = icons(data)
    lines = [f"// {HEADER}", "", "/// The SF Symbol for each action and place, the same idea on every client (design/tokens.json › icons).",
             "enum Symbol {"]
    for name, i in entries.items():
        lines.append(f"    /// {i['use']}.")
        lines.append(f'    static let {name} = "{i["sf"]}"')
    lines.append("")
    lines.append("    /// Every name above, so a debug build can check each one exists on this system.")
    lines.append("    static let all = [" + ", ".join(entries) + "]")
    lines.append("}")
    return "\n".join(lines) + "\n"


def kotlin_icons(data):
    entries = icons(data)
    imports = set()
    for i in entries.values():
        parts = i["material"].split(".")
        imports.add("import androidx.compose.material.icons." + ".".join(p.lower() for p in parts[:-1]) + "." + parts[-1])
    lines = [f"// {HEADER}", "", "package io.github.flexarr.ui.theme", "", "import androidx.compose.material.icons.Icons",
             *sorted(imports), "import androidx.compose.ui.graphics.vector.ImageVector", "",
             "/** The icon for each action and place, the same idea on every client (design/tokens.json › icons). */",
             "object FlexarrIcons {"]
    for name, i in entries.items():
        lines.append(f"    /** {i['use']}. */")
        lines.append(f"    val {name}: ImageVector get() = Icons.{i['material']}")
    lines.append("}")
    return "\n".join(lines) + "\n"


PATH_TOKEN = re.compile(r"[MLCZ]|-?\d+(?:\.\d+)?")
PATH_ARGS = {"M": 2, "L": 2, "C": 6, "Z": 0}


def path_commands(d):
    """An SVG path of absolute M, L, C and Z only, as [(command, [numbers])]; anything else is an error."""
    if re.sub(r"[MLCZ\d\s.\-]", "", d):
        raise ValueError(f"path uses something other than absolute M, L, C, Z: {d[:40]}…")
    tokens, commands = PATH_TOKEN.findall(d), []
    i = 0
    while i < len(tokens):
        op = tokens[i]
        if op not in PATH_ARGS:
            raise ValueError(f"path has a number where a command belongs: {d[:40]}…")
        raw = tokens[i + 1:i + 1 + PATH_ARGS[op]]
        if len(raw) != PATH_ARGS[op] or any(t in PATH_ARGS for t in raw):
            raise ValueError(f"path command {op} needs {PATH_ARGS[op]} numbers: {d[:40]}…")
        commands.append((op, [float(t) for t in raw]))
        i += 1 + PATH_ARGS[op]
    return commands


def load_scores(path=SCORES):
    data = json.loads(Path(path).read_text())
    for name, m in data["marks"].items():
        if not NAME.match(name):
            raise ValueError(f"score mark name {name!r} isn't camelCase")
        for p in m["paths"]:
            if not HEX.match(p["fill"]):
                raise ValueError(f"score mark {name}: {p['fill']!r} isn't #RRGGBB")
            path_commands(p["d"])
    return data


def web_scores(scores):
    lines = [f"// {SCORES_HEADER}", "",
             "/** A score's source mark: a viewBox and filled paths (docs/design.md › Scores). */",
             "export type ScoreMarkDrawing = { width: number; height: number; paths: readonly { fill: string; d: string }[] }",
             "", "export const scoreMarks = {"]
    for name, m in scores["marks"].items():
        lines.append(f"  /** {m['use']} */")
        lines.append(f"  {name}: {{ width: {num(m['width'])}, height: {num(m['height'])}, paths: [")
        for p in m["paths"]:
            lines.append(f"    {{ fill: '{p['fill'].lower()}', d: '{p['d']}' }},")
        lines.append("  ] },")
    lines.append("} as const satisfies Record<string, ScoreMarkDrawing>")
    return "\n".join(lines) + "\n"


def swift_point(x, y):
    return f"CGPoint(x: {num(x)}, y: {num(y)})"


def swift_scores(scores):
    lines = [f"// {SCORES_HEADER}", "", "import SwiftUI", "",
             "/// A score's source mark: a viewBox and filled paths (docs/design.md › Scores).",
             "struct ScoreMarkDrawing {", "    let width: CGFloat", "    let height: CGFloat",
             "    let paths: [(fill: Color, path: Path)]", "}", "", "enum ScoreMarks {"]
    for name, m in scores["marks"].items():
        lines.append(f"    /// {m['use']}.")
        lines.append(f"    static let {name} = ScoreMarkDrawing(width: {num(m['width'])}, height: {num(m['height'])}, paths: [")
        for p in m["paths"]:
            h = p["fill"].lstrip("#")
            r, g, b = (int(h[i:i + 2], 16) for i in (0, 2, 4))
            lines.append(f"        (Color(.sRGB, red: {r} / 255, green: {g} / 255, blue: {b} / 255), Path {{ p in")
            for op, a in path_commands(p["d"]):
                if op == "M":
                    lines.append(f"            p.move(to: {swift_point(*a)})")
                elif op == "L":
                    lines.append(f"            p.addLine(to: {swift_point(*a)})")
                elif op == "C":
                    lines.append(f"            p.addCurve(to: {swift_point(a[4], a[5])}, control1: {swift_point(a[0], a[1])}, "
                                 f"control2: {swift_point(a[2], a[3])})")
                else:
                    lines.append("            p.closeSubpath()")
            lines.append("        }),")
        lines.append("    ])")
    lines.append("}")
    return "\n".join(lines) + "\n"


def kotlin_scores(scores):
    lines = [f"// {SCORES_HEADER}", "", "package io.github.flexarr.ui.theme", "",
             "import androidx.compose.ui.graphics.Color", "import androidx.compose.ui.graphics.SolidColor",
             "import androidx.compose.ui.graphics.vector.ImageVector", "import androidx.compose.ui.graphics.vector.addPathNodes",
             "import androidx.compose.ui.unit.dp", "",
             "/** The source mark beside a score (docs/design.md › Scores). */", "object ScoreMarks {"]
    for name, m in scores["marks"].items():
        lines.append(f"    /** {m['use']}. */")
        lines.append(f'    val {name}: ImageVector by lazy {{')
        lines.append(f'        mark("{name}", {num(m["width"])}f, {num(m["height"])}f, listOf(')
        for p in m["paths"]:
            lines.append(f'            0xFF{p["fill"].lstrip("#").upper()} to "{p["d"]}",')
        lines.append("        ))")
        lines.append("    }")
    lines += ["", "    private fun mark(name: String, width: Float, height: Float, paths: List<Pair<Long, String>>): ImageVector =",
              "        ImageVector.Builder(name, width.dp, height.dp, width, height).apply {",
              "            for ((fill, d) in paths) addPath(addPathNodes(d), fill = SolidColor(Color(fill)))",
              "        }.build()", "}"]
    return "\n".join(lines) + "\n"


def outputs(data, scores=None):
    scores = scores or load_scores()
    return {CSS: css(data), SWIFT: swift(data), KOTLIN: kotlin(data),
            WEB_ICONS: web_icons(data), SWIFT_ICONS: swift_icons(data), KOTLIN_ICONS: kotlin_icons(data),
            WEB_SCORES: web_scores(scores), SWIFT_SCORES: swift_scores(scores), KOTLIN_SCORES: kotlin_scores(scores)}


def main(argv):
    data = load()
    stale = []
    for path, text in outputs(data).items():
        if "--check" in argv:
            if not path.exists() or path.read_text() != text:
                stale.append(str(path.relative_to(ROOT)))
        else:
            path.write_text(text)
    if stale:
        print("Out of date (run `make tokens`): " + ", ".join(stale), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
