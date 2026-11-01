import copy
import json
import unittest
from pathlib import Path

import tokens


class TokensTests(unittest.TestCase):
    def setUp(self):
        self.data = tokens.load()

    def test_every_colour_reaches_every_client(self):
        css, swift, kotlin = tokens.css(self.data), tokens.swift(self.data), tokens.kotlin(self.data)
        for name, c in self.data["colors"].items():
            self.assertIn(f"--{tokens.kebab(name)}: {c['hex'].lower()};", css)
            self.assertIn(f"static let {name} = Color(", swift)
            self.assertIn(f"val {name} = Color(0xFF{c['hex'][1:].upper()})", kotlin)

    def test_hex_round_trips_into_swift_components(self):
        swift = tokens.swift(self.data)
        self.assertIn("static let room = Color(.sRGB, red: 27 / 255, green: 23 / 255, blue: 32 / 255)", swift)

    def test_radii_and_mix(self):
        css, kotlin = tokens.css(self.data), tokens.kotlin(self.data)
        self.assertIn("--radius-art: 4px;", css)
        self.assertIn("val radiusPanel = 14.dp", kotlin)
        self.assertIn("--main-button-mix: 68%;", css)

    def test_missing_token_fails(self):
        bad = copy.deepcopy(self.data)
        del bad["colors"]["record"]
        with self.assertRaisesRegex(ValueError, "missing colour record"):
            tokens.validate(bad)

    def test_bad_hex_fails(self):
        bad = copy.deepcopy(self.data)
        bad["colors"]["room"]["hex"] = "#123"
        with self.assertRaisesRegex(ValueError, "isn't #RRGGBB"):
            tokens.validate(bad)

    def test_every_icon_reaches_every_client(self):
        web, swift, kotlin = tokens.web_icons(self.data), tokens.swift_icons(self.data), tokens.kotlin_icons(self.data)
        for name, i in tokens.icons(self.data).items():
            self.assertIn(f"  {name}: {i['web']},", web)
            self.assertIn(f'static let {name} = "{i["sf"]}"', swift)
            self.assertIn(f"val {name}: ImageVector get() = Icons.{i['material']}", kotlin)

    def test_material_imports(self):
        kotlin = tokens.kotlin_icons(self.data)
        self.assertIn("import androidx.compose.material.icons.filled.PlayArrow", kotlin)
        self.assertIn("import androidx.compose.material.icons.outlined.Home", kotlin)

    def test_unknown_web_icon_fails(self):
        bad = copy.deepcopy(self.data)
        bad["icons"]["play"]["web"] = "NoSuchIcon"
        with self.assertRaisesRegex(ValueError, "no icon 'NoSuchIcon'"):
            tokens.validate(bad)

    def test_bad_material_name_fails(self):
        bad = copy.deepcopy(self.data)
        bad["icons"]["play"]["material"] = "PlayArrow"
        with self.assertRaisesRegex(ValueError, "isn't Style.Name"):
            tokens.validate(bad)

    def test_generated_files_are_current(self):
        for path, text in tokens.outputs(self.data).items():
            self.assertEqual(Path(path).read_text(), text, f"{path} is stale: run make tokens")


if __name__ == "__main__":
    unittest.main()
