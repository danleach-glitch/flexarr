# Flexarr's languages

Every word Flexarr shows comes from here. `packs/en.json` holds every string, keyed by its own English; each other
`packs/<code>.json` is a translation of it. The full story is in [docs/languages.md](../docs/languages.md).

| File | Who writes it |
| --- | --- |
| `packs/en.json` | `make strings`, from the code. Never edit it by hand: change the English in the code. |
| `en.notes.json` | People: a note for translators on any string (`"Play": "The main button on a movie's page"`). |
| `packs/<code>.json` | Translators: English key → translation. Leave out what isn't translated yet. |
| `languages.json` | People: each language's native name and direction (`ltr` or `rtl`). |
| `state/` | `make strings`: versions, where each string is used, and translations that are out of date. |
| `cldr/plurals.json` | CLDR's plural rules (Unicode License v3). |
| `tests/format-cases.json` | The cases every platform's formatter must agree on. |

## Adding a language

1. Add it to `languages.json`, e.g. `"de": {"name": "Deutsch", "direction": "ltr"}`. Codes are BCP 47: `de`,
   `pt-BR`, `zh-Hans`.
2. Create `packs/de.json` and translate, a string at a time:
   ```json
   {
     "Mark as watched": "Als gesehen markieren",
     "{count, plural, one {# season} other {# seasons}}": "{count, plural, one {# Staffel} other {# Staffeln}}"
   }
   ```
3. Run `make strings`. It checks every translation (same `{values}` as the English, the right plural forms for the
   language, no markup) and records the pack's new version.
4. Open a pull request. On merge, flexarr.io/languages publishes it, Flexarr servers pick it up within a day, and the
   next app releases build it in.

## Writing translations

- Keep every `{value}` exactly as it is in the English; move it wherever the sentence needs it.
- Plurals use your language's forms: `one`, `few`, `many`, `other` in Polish, for example. `#` is the number.
- `'{'` and `'}'` print braces, `''` prints an apostrophe. A lone apostrophe (`l'épisode`) is fine.
- A key starting `word::` has a context: `music::Play` is *Play* on a music screen.
- Use the same words for the same things everywhere; Flexarr's English words are in
  [docs/design.md](../docs/design.md#words).

## Weblate

These files are laid out for Weblate's *JSON file* format: monolingual, `en.json` as the base, file mask
`languages/packs/*.json`. Weblate's *needs editing* state matches
`make strings`' out-of-date marking.
