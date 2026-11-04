#!/usr/bin/env python3
"""A pseudo-language for testing (docs/languages.md, *Testing*): every English string accented, 30% longer and
wrapped in ⟦ ⟧, written as a ready-to-serve pack (code en-XA, so English's plural rules apply). Never published or
bundled. Point it at a server's data folder and that server offers it:

    python3 scripts/i18n/pseudo.py <data folder>/languages
"""
import hashlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import icu  # noqa: E402
import strings  # noqa: E402

ACCENTS = str.maketrans(
    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ",
    "àƀçđéƒĝĥíĵķĺḿñöƥʠŕšţûṽŵẋýžÀßÇĐÉƑĜĤÍĴĶĹḾÑÖƤǪŔŠŢÛṼŴẊÝŽ",
)
CODE = "en-XA"


def pseudo(text):
    """One piece of plain text, accented."""
    return text.translate(ACCENTS)


def build(english, scopes):
    messages = {}
    for key, text in english.items():
        parts = icu.parse(text)
        body = icu.unparse(parts, transform=pseudo)
        letters = sum(c.isalpha() for t in icu.texts(parts) for c in t)
        messages[key] = f"⟦{body}{'~' * max(1, round(letters * 0.3))}⟧"
    return {
        "code": CODE, "name": "Pseudo (test)", "direction": "ltr", "format": 1, "version": 1,
        "coverage": {s: 1.0 for s in strings.SCOPES}, "messages": messages, "scopes": scopes,
    }


def main(argv):
    if len(argv) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    english = json.loads((strings.PACKS / "en.json").read_text(encoding="utf-8"))
    scopes = json.loads((strings.STATE / "en.json").read_text(encoding="utf-8"))["scopes"]
    pack = build(english, scopes)
    text = json.dumps(pack, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n"
    out = Path(argv[0])
    out.mkdir(parents=True, exist_ok=True)
    (out / f"{CODE}.json").write_text(text, encoding="utf-8")
    print(f"{out / (CODE + '.json')}: {len(english)} strings, sha256 {hashlib.sha256(text.encode()).hexdigest()[:12]}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
