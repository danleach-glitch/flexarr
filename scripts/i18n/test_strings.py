"""python3 -m unittest discover -s scripts/i18n"""
import json
import re
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import icu  # noqa: E402
import plurals  # noqa: E402
import scan  # noqa: E402
import strings  # noqa: E402

RULES = plurals.load()


def samples(rule):
    """The integer and decimal samples CLDR gives after @integer / @decimal (ranges as their ends)."""
    out = []
    for part in re.split(r"@(?:integer|decimal)", rule)[1:]:
        for item in part.split(","):
            item = item.strip().rstrip("…").strip()
            if not item:
                continue
            for end in item.split("~"):
                if re.fullmatch(r"\d+(\.\d+)?", end):
                    out.append(end)
    return out


class PluralRuleTests(unittest.TestCase):
    def test_every_cldr_sample_picks_its_category(self):
        data = json.loads(plurals.SOURCE.read_text())["cardinal"]
        checked = 0
        for loc, cats in data.items():
            for cat, rule in cats.items():
                for s in samples(rule):
                    # A decimal sample with trailing zeros (1.0) keeps its fraction digits in CLDR; our numbers
                    # don't (a Double has no trailing zeros), so only samples a Double can carry are checked.
                    if "." in s and s.endswith("0"):
                        continue
                    self.assertEqual(plurals.category(RULES, loc, float(s)), cat, f"{loc} {s}")
                    checked += 1
        self.assertGreater(checked, 2000)

    def test_locale_fallback(self):
        self.assertEqual(plurals.resolve(RULES, "pt-PT"), "pt-PT")
        self.assertEqual(plurals.resolve(RULES, "de-AT"), "de")
        self.assertEqual(plurals.resolve(RULES, "zz"), "und")
        self.assertEqual(plurals.categories(RULES, "pl"), {"one", "few", "many", "other"})


class MessageTests(unittest.TestCase):
    def test_parses_plurals_and_selects(self):
        parts = icu.parse("{count, plural, =0 {No episodes} one {# episode} other {# episodes}} in {name}")
        self.assertEqual(icu.args(parts), {"count": "plural", "name": "simple"})

    def test_apostrophes(self):
        self.assertEqual(icu.parse("Don't"), ["Don't"])
        self.assertEqual(icu.parse("'{'braces'}' and ''"), ["{braces} and '"])

    def test_errors(self):
        for bad in ["{", "}", "{count, plural, one {x}}", "{a, date}", "{1}", "{a, select, =1 {x} other {y}}"]:
            with self.assertRaises(icu.MessageError, msg=bad):
                icu.parse(bad)

    def test_unparse_round_trips(self):
        for m in ["Plain", "{n, plural, one {# item} other {# items}}", "'{'x'}'", "It''s {name}"]:
            self.assertEqual(icu.parse(icu.unparse(icu.parse(m))), icu.parse(m))


class ScanTests(unittest.TestCase):
    def test_typescript(self):
        src = "t('Mark as watched'); t(\"Play\", { ctx: 'music' }); x.t('no'); t(label); t.rich('Hi {name}', {})"
        keys = [k for k, _, e in scan.scan_text(src, "ts", scan.SOURCES[0].call)]
        self.assertEqual(keys, ["Mark as watched", "music::Play", "Hi {name}"])

    def test_interpolation_is_an_error(self):
        _, _, err = scan.scan_text("t(`Hi ${name}`)", "ts", scan.SOURCES[0].call)[0]
        self.assertIn("interpolation", err)
        _, _, err = scan.scan_text('t("Hi \\(name)")', "swift", scan.SOURCES[1].call)[0]
        self.assertIn("interpolation", err)
        _, _, err = scan.scan_text('t("Hi $name")', "kotlin", scan.SOURCES[3].call)[0]
        self.assertIn("templates", err)
        _, _, err = scan.scan_text('Loc.T($"Hi {name}")', "cs", scan.SOURCES[4].call)[0]
        self.assertIn("interpolated", err)

    def test_swift_kotlin_csharp_contexts(self):
        self.assertEqual(scan.scan_text('t("Play", ctx: "music")', "swift", scan.SOURCES[1].call)[0][0], "music::Play")
        self.assertEqual(scan.scan_text('t("Play", ctx = "music")', "kotlin", scan.SOURCES[3].call)[0][0], "music::Play")
        self.assertEqual(scan.scan_text('Loc.T("Play", ctx: "music")', "cs", scan.SOURCES[4].call)[0][0], "music::Play")

    def test_escapes(self):
        self.assertEqual(scan.scan_text('t("Say \\"hi\\"\\u{2019}")', "swift", scan.SOURCES[1].call)[0][0], "Say \"hi\"’")


class TranslationTests(unittest.TestCase):
    def check(self, code, english, text):
        problems = strings.Problems()
        ok = strings.validate_translation(code, english, text, icu.parse(english), RULES, problems)
        return ok, problems

    def test_good(self):
        ok, _ = self.check("pl", "{n, plural, one {# season} other {# seasons}}",
                           "{n, plural, one {# sezon} few {# sezony} many {# sezonów} other {# sezonu}}")
        self.assertTrue(ok)

    def test_values_must_match(self):
        ok, problems = self.check("de", "Resume from {time}", "Fortsetzen ab {zeit}")
        self.assertFalse(ok)
        self.assertIn("values differ", problems[0])

    def test_categories_must_exist(self):
        ok, problems = self.check("ja", "{n, plural, one {# item} other {# items}}", "{n, plural, one {#} other {#}}")
        self.assertFalse(ok)
        self.assertIn("doesn't have", problems[0])

    def test_no_markup(self):
        ok, _ = self.check("de", "Play", "<b>Abspielen</b>")
        self.assertFalse(ok)

    def test_carry_over_marks_out_of_date(self):
        packs = {"de": {"Mark as watched": "Als gesehen markieren"}}
        outdated = {}
        moves = strings.carry_over(["Mark as watched"], ["Mark as watched!", "Something else"], packs, outdated)
        self.assertEqual(moves, {"Mark as watched": "Mark as watched!"})
        self.assertEqual(packs["de"], {"Mark as watched!": "Als gesehen markieren"})
        self.assertIn("Mark as watched!", outdated["de"])

    def test_carry_over_keeps_contexts_apart(self):
        packs = {"de": {"music::Play": "Abspielen"}}
        self.assertEqual(strings.carry_over(["music::Play"], ["Play"], packs, {}), {})


if __name__ == "__main__":
    unittest.main()
