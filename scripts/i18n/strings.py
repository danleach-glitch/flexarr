#!/usr/bin/env python3
"""`make strings`: collect every looked-up string from the code into languages/packs/en.json, check every pack, carry
translations over to changed English (marked out of date), and write what's built from them: the plural rules for
every platform, the packs bundled into the server and apps, the native files the operating systems read, and
flexarr.io/languages. `--check` fails when any of it is stale, without writing. See docs/languages.md."""
import difflib
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import icu  # noqa: E402
import plurals  # noqa: E402
import scan  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
LANG = ROOT / "languages"
PACKS = LANG / "packs"
STATE = LANG / "state"
REGISTRY = LANG / "languages.json"
NOTES = LANG / "en.notes.json"

SCOPES = ["web", "apple", "android", "server"]
SCOPE_LETTER = {"web": "w", "apple": "a", "android": "n", "server": "s"}
MAX_PACK_BYTES = 2_000_000
SIMILAR = 0.75
MARKUP = re.compile(r"<\s*/?\s*[A-Za-z!]|&[A-Za-z]+;|&#\d+;")
CODE = re.compile(r"^[a-z]{2,3}(-[A-Z][a-z]{3})?(-([A-Z]{2}|[0-9]{3}))?$")

TS_PLURALS = ROOT / "frontend/src/i18n/plurals.generated.ts"
SWIFT_PLURALS = ROOT / "clients/apple/FlexarrKit/Sources/FlexarrKit/Localization/PluralRules.generated.swift"
KOTLIN_PLURALS = ROOT / "clients/android/core/src/main/kotlin/io/github/flexarr/core/i18n/PluralRules.kt"
CS_PLURALS = ROOT / "src/Flexarr.Core/Localization/PluralRules.generated.cs"
APPLE_PACKS = ROOT / "clients/apple/FlexarrKit/Sources/FlexarrKit/Resources/Languages"
ANDROID_PACKS = ROOT / "clients/android/core/src/main/resources/languages"
SERVER_PACKS = ROOT / "src/Flexarr.Core/Localization/Packs"
SITE = ROOT / "site/languages"


def dump(data):
    return json.dumps(data, ensure_ascii=False, indent=1, sort_keys=True) + "\n"


def read_json(path, default):
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else default


def sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


class Problems(list):
    def add(self, where, what):
        self.append(f"{where}: {what}")


# ---- the model ---------------------------------------------------------------------------------------------------

def load_packs():
    """{code: {key: translation}} for every pack except English and the git-ignored pseudo-pack."""
    return {p.stem: read_json(p, {}) for p in sorted(PACKS.glob("*.json")) if p.stem not in ("en", "qps")}


def validate_english(keys, problems):
    parsed = {}
    for key in sorted(keys):
        try:
            parsed[key] = icu.parse(scan.english(key))
        except icu.MessageError as e:
            problems.add(f"en: {key!r}", str(e))
    return parsed


def validate_translation(code, key, text, english_parts, rules, problems):
    where = f"{code}: {key!r}"
    if not isinstance(text, str) or not text.strip():
        problems.add(where, "empty translation")
        return False
    try:
        parts = icu.parse(text)
    except icu.MessageError as e:
        problems.add(where, str(e))
        return False
    want, got = icu.args(english_parts), icu.args(parts)
    if set(want) != set(got):
        missing, extra = sorted(set(want) - set(got)), sorted(set(got) - set(want))
        problems.add(where, "values differ from the English" + (f"; missing {missing}" if missing else "")
                     + (f"; unknown {extra}" if extra else ""))
        return False
    for name, kind in want.items():
        if (kind == "plural") != (got[name] == "plural") or (kind == "select") != (got[name] == "select"):
            problems.add(where, f"{{{name}}} is a {kind} in English but a {got[name]} here")
            return False
    allowed = plurals.categories(rules, code)
    for name, kind, sels in icu.selectors(parts):
        if kind == "plural":
            bad = sorted(s for s in sels if not s.startswith("=") and s not in allowed)
            if bad:
                problems.add(where, f"{{{name}}} uses {bad}, which {code} doesn't have (it has {sorted(allowed)})")
                return False
    if any(MARKUP.search(t) for t in icu.texts(parts)):
        problems.add(where, "no markup in translations")
        return False
    return True


def carry_over(removed, added, packs, outdated):
    """Moves each pack's translation of removed English to the most similar added English with the same context,
    marking it out of date. Returns {old: new} for the moves made."""
    moves = {}
    pairs = []
    for old in removed:
        old_ctx = old.split("::")[0] if "::" in old else ""
        for new in added:
            new_ctx = new.split("::")[0] if "::" in new else ""
            if old_ctx != new_ctx:
                continue
            ratio = difflib.SequenceMatcher(None, scan.english(old), scan.english(new)).ratio()
            if ratio >= SIMILAR:
                pairs.append((ratio, old, new))
    used_old, used_new = set(), set()
    for ratio, old, new in sorted(pairs, reverse=True):
        if old in used_old or new in used_new:
            continue
        used_old.add(old)
        used_new.add(new)
        moves[old] = new
        for code, pack in packs.items():
            if old in pack and new not in pack:
                pack[new] = pack.pop(old)
                outdated.setdefault(code, {})[new] = sha(pack[new])[:12]
    return moves


def build(now=None, write_check=False):
    """({path: text} of everything make strings writes, problems)."""
    problems = Problems()
    registry = read_json(REGISTRY, {})
    for code, info in registry.items():
        if not CODE.match(code):
            problems.add("languages.json", f"{code!r} isn't a BCP 47 code like de, pt-BR or zh-Hans")
        if info.get("direction") not in ("ltr", "rtl") or not info.get("name"):
            problems.add("languages.json", f"{code} needs a name and a direction (ltr or rtl)")
    rules = plurals.load()

    import words as shared  # noqa: E402  (languages/words.json)
    word_keys = shared.load()
    for prob in shared.validate(word_keys):
        problems.append(prob)
    scan.WORDS = word_keys
    typed = {}
    found, scan_errors = scan.scan(typed=typed)
    by_key = {v: k for k, v in word_keys.items()}
    for key, places in sorted(typed.items()):
        if key in by_key:
            for place in places:
                problems.append(f"{place}: {key!r} is a shared word: use Words.{by_key[key]}")
    # Every shared word is offered to every platform, used yet or not.
    for key in word_keys.values():
        found.setdefault(key, set()).update(SCOPES)
    import native  # noqa: E402  (the text the operating systems read themselves)
    for key, scopes in native.collect().items():
        found.setdefault(key, set()).update(scopes)
    for e in scan_errors:
        problems.append(e)
    english = validate_english(found, problems)

    old_en = read_json(PACKS / "en.json", {})
    packs = load_packs()
    state = {code: read_json(STATE / f"{code}.json", {}) for code in packs}
    outdated = {code: dict(s.get("outdated", {})) for code, s in state.items()}

    removed = sorted(set(old_en) - set(found))
    added = sorted(set(found) - set(old_en))
    carry_over(removed, added, packs, outdated)

    for code in packs:
        if code not in registry:
            problems.add(f"packs/{code}.json", "isn't in languages.json")

    built = {}
    for code, pack in packs.items():
        kept = {}
        for key, text in sorted(pack.items()):
            if key not in english:
                continue  # English no longer used: dropped
            if validate_translation(code, key, text, english[key], rules, problems):
                kept[key] = text
        stale = {k: h for k, h in outdated.get(code, {}).items() if k in kept and sha(kept[k])[:12] == h}
        packs[code], outdated[code] = kept, stale
        content = dump({"messages": kept, "outdated": stale})
        version = state[code].get("version", 0)
        if state[code].get("hash") != sha(content):
            version = int((now or datetime.now(timezone.utc)).strftime("%Y%m%d%H%M%S"))
        state[code] = {"hash": sha(content), "outdated": stale, "version": version}
        live = {k: v for k, v in kept.items() if k not in stale}
        built[code] = live

    out = {}
    out[PACKS / "en.json"] = dump({k: scan.english(k) for k in found})
    out[STATE / "en.json"] = dump({"scopes": {k: "".join(SCOPE_LETTER[s] for s in SCOPES if s in found[k]) for k in found}})
    for code in packs:
        out[PACKS / f"{code}.json"] = dump(packs[code])
        out[STATE / f"{code}.json"] = dump(state[code])
    out.update(shared.outputs(word_keys))
    out[TS_PLURALS] = plurals.typescript(rules)
    out[SWIFT_PLURALS] = plurals.swift(rules)
    out[KOTLIN_PLURALS] = plurals.kotlin(rules, "io.github.flexarr.core.i18n")
    out[CS_PLURALS] = plurals.csharp(rules, "Flexarr.Core.Localization")

    index = []
    for code, live in sorted(built.items()):
        info = registry.get(code, {"name": code, "direction": "ltr"})
        meta = {"code": code, "name": info["name"], "direction": info["direction"], "version": state[code]["version"]}
        coverage = {}
        for s in SCOPES:
            total = [k for k in found if s in found[k]]
            coverage[s] = round(sum(1 for k in total if k in live) / len(total), 3) if total else 0.0
        full = {**meta, "format": 1, "coverage": coverage, "messages": live,
                "scopes": {k: "".join(SCOPE_LETTER[s] for s in SCOPES if s in found[k]) for k in live}}
        text = json.dumps(full, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n"
        if len(text.encode("utf-8")) > MAX_PACK_BYTES:
            problems.add(f"packs/{code}.json", f"is over {MAX_PACK_BYTES // 1_000_000} MB")
        out[SERVER_PACKS / f"{code}.json"] = text
        out[SITE / f"{code}.json"] = text
        for scope, folder in (("apple", APPLE_PACKS), ("android", ANDROID_PACKS)):
            scoped = {k: v for k, v in live.items() if scope in found[k]}
            out[folder / f"{code}.json"] = json.dumps({**meta, "format": 1, "messages": scoped}, ensure_ascii=False,
                                                      sort_keys=True, separators=(",", ":")) + "\n"
        index.append({**meta, "coverage": coverage, "sha256": sha(text), "size": len(text.encode("utf-8"))})
    out[SITE / "index.json"] = dump({"format": 1, "languages": index})
    # Keep the bundled folders in the build even with no packs yet.
    for folder in (APPLE_PACKS, ANDROID_PACKS, SERVER_PACKS, SITE):
        out[folder / ".gitkeep"] = ""

    out.update(native.outputs(found, built, registry))
    return out, problems


def near_duplicates(keys):
    """Groups of English that differ only in case, punctuation or spacing (Up Next / Up next, couldn't / couldn’t),
    the candidates for one string or a shared word. Contexts count: music::Play isn't a duplicate of Play."""
    groups = {}
    for key in keys:
        ctx = key.split("::")[0] if scan.CONTEXT_KEY.match(key) else ""
        norm = re.sub(r"[\s.,!?…:;’'\"“”()–—-]+", " ", scan.english(key)).strip().casefold()
        groups.setdefault((ctx, norm), []).append(key)
    def usual(key):
        # A question or "…" button beside its plain form, or a capital only at the start, is a different use, not a slip.
        e = re.sub(r"[\s.?…]+$", "", scan.english(key))
        return e[:1].lower() + e[1:]

    return [sorted(g) for g in groups.values() if len(g) > 1 and len({usual(k) for k in g}) > 1]


def stale_files(out):
    """Generated files that would be written differently, plus bundled packs that shouldn't be there any more."""
    stale = [p for p, text in out.items() if not p.exists() or p.read_text(encoding="utf-8") != text]
    for folder in (APPLE_PACKS, ANDROID_PACKS, SERVER_PACKS, SITE):
        if folder.exists():
            stale += [p for p in folder.glob("*.json") if p not in out]
    return stale


def main(argv):
    check = "--check" in argv
    out, problems = build()
    if problems:
        print("make strings found problems:", file=sys.stderr)
        for p in problems:
            print(f"  {p}", file=sys.stderr)
        return 1
    if check:
        stale = stale_files(out)
        if stale:
            print("These are stale; run `make strings`:", file=sys.stderr)
            for p in stale:
                print(f"  {p.relative_to(ROOT)}", file=sys.stderr)
            return 1
        return 0
    for folder in (APPLE_PACKS, ANDROID_PACKS, SERVER_PACKS, SITE):
        if folder.exists():
            for p in folder.glob("*.json"):
                if p not in out:
                    p.unlink()
    for path, text in out.items():
        if not path.exists() or path.read_text(encoding="utf-8") != text:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
    english = json.loads(out[PACKS / "en.json"])
    print(f"{len(english)} strings; packs: {', '.join(c for c in load_packs()) or 'none yet'}")
    dupes = near_duplicates(english)
    if dupes and "--duplicates" in argv:
        print("Near-duplicates (make them one string, or a shared word in languages/words.json):")
        for g in dupes:
            print("  " + "  |  ".join(repr(k) for k in g))
    elif dupes:
        print(f"{len(dupes)} near-duplicate groups: `python3 scripts/i18n/strings.py --duplicates` lists them")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
