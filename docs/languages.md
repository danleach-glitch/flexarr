# Languages

Flexarr in your language: every word the server and the apps show comes from one English source file, and other
languages are packs translated from it. Packs are built into every app release and can also be downloaded between
releases, so a new or corrected translation reaches people without waiting for the App Store.

This page is the plan and the reference for how it works. The checklist it was built from is the *Languages Brief*.

## The pieces

| Piece | Where | What it does |
| --- | --- | --- |
| English source | `languages/packs/en.json` | Every string, keyed by its own English text |
| Notes for translators | `languages/en.notes.json` | Optional: where a string appears, what a placeholder holds |
| Packs | `languages/packs/<code>.json` | One per language: English key → translation |
| Pack state | `languages/state/<code>.json` | Written by `make strings`: version, scopes, out-of-date strings |
| Registry | `languages/languages.json` | Each language's native name and direction |
| Plural rules | `languages/cldr/plurals.json` | CLDR's plural rules, turned into code for every platform |
| Formatter tests | `languages/tests/format-cases.json` | The cases all four formatters must agree on |
| Tooling | `scripts/i18n/` | `make strings`, `make languages`, the pseudo-pack, the extraction scripts |
| Published packs | `site/languages/` → flexarr.io/languages | The index servers read, and the packs they download |

## Writing a string

Code never holds a bare English sentence that people read. It calls the lookup, with the English itself as the key:

| Platform | Plain | With values | With a context |
| --- | --- | --- | --- |
| Web | `t('Mark as watched')` | `t('Resume from {time}', { time })` | `t('Play', { ctx: 'music' })` |
| Swift | `t("Mark as watched")` | `t("Resume from {time}", ["time": time])` | `t("Play", ctx: "music")` |
| Kotlin | `t("Mark as watched")` | `t("Resume from {time}", "time" to time)` | `t("Play", ctx = "music")` |
| C# | `Loc.T("Mark as watched")` | `Loc.T("Resume from {time}", new { time })` | `Loc.T("Play", ctx: "music")` |

- The string must be a plain literal: no interpolation, no concatenation, no variables. `make strings` reads it out of
  the code, so it has to be able to see it.
- Values go in `{braces}` and are named, never numbered. Translators can move them around the sentence.
- A **context** (`ctx`) is only for when the same English means two different things (*Play* a film, *Play* a
  playlist that's still loading). Its key in the files is `music::Play`. Contexts are lowercase words with hyphens.
- Write whole sentences. Never build a sentence from translated pieces (`t('Added to') + ' ' + name`): word order
  differs between languages. Put the piece in as a value instead, or on the web use `t.rich` to drop in markup:
  `t.rich('Resume {title} from {time}', { title: <b>{title}</b>, time })`.
- Names people chose (library names, profile names), titles and metadata are never translated.

### Plurals and choices

Messages use the ICU MessageFormat subset that every platform's formatter supports:

```
{count, plural, one {# season} other {# seasons}}
{count, plural, =0 {No episodes} one {# episode} other {# episodes}}
{kind, select, album {Album} single {Single} other {Release}}
{size, number}
'{' and '}' print braces; '' prints an apostrophe
```

`#` inside a plural is the number, formatted for the language. English needs only `one` and `other`; a translation
uses its own language's categories (Polish has `one`, `few`, `many` and `other`; Arabic six). `make strings` checks a
translation's categories are the ones its language has. `=0` and other exact matches are allowed in any language.

Plural rules come from CLDR (`languages/cldr/plurals.json`), turned into code for TypeScript, Swift, Kotlin and C# by
`make strings`, so every platform picks the same form.

## The files

`en.json` is a flat map, sorted, written by `make strings`:

```json
{
  "Mark as watched": "Mark as watched",
  "music::Play": "Play",
  "{count, plural, one {# season} other {# seasons}}": "{count, plural, one {# season} other {# seasons}}"
}
```

A pack is the same shape, with translations as values, and only the strings translated so far:

```json
{
  "Mark as watched": "Als gesehen markieren",
  "{count, plural, one {# season} other {# seasons}}": "{count, plural, one {# Staffel} other {# Staffeln}}"
}
```

This is the shape Weblate calls a *JSON file* with `en.json` as the monolingual base, so connecting Weblate later is
pointing it at `languages/packs/*.json`.

`languages.json` lists the languages that exist:

```json
{ "de": { "name": "Deutsch", "direction": "ltr" }, "ar": { "name": "العربية", "direction": "rtl" } }
```

Codes are BCP 47 (`de`, `pt-BR`, `zh-Hans`). The native name is what the picker shows.

`state/<code>.json` is written by the tooling, never by hand: the pack's `version` (a UTC timestamp, bumped whenever
its translations change), each string's **scopes** (for `en`: which codebases use it: `web`, `apple`, `android`,
`server`) and the pack's **out-of-date** strings.

## Out of date

The key is the English, so changing the English makes a new key. `make strings` notices a string that disappeared
and a similar new one (same context, mostly the same words) appearing in the same run, moves each pack's translation
to the new key and marks it out of date, with a hash of the translation as it stood. Out-of-date translations aren't
published and aren't built into the apps: people see the new English until a translator updates it. When the
translation's text changes, it's no longer out of date. Weblate's *needs editing* state is the same idea.

## Checks

`make strings` (`scripts/i18n/strings.py`) does all of this, and CI runs it with `--check`, which fails when anything
it would write is different:

- collects every lookup from `frontend/src`, `clients/apple`, `clients/android` and `src/` into `en.json`, adding new
  strings and dropping unused ones, and records each string's scopes;
- fails on a lookup it can't read (interpolation, a variable, a bad context);
- parses every message, in English and every pack, and fails on a syntax error;
- checks each translation has exactly English's values, plural categories that exist in its language, and no markup;
- carries translations over to changed English and marks them out of date;
- caps each pack's size;
- writes the plural rules for all four platforms and the bundled files below.

## Built in and downloaded

**Built in.** Each app and the server carry every pack that exists when they're built:

- the web app bundles `en` in its code and the other packs as files beside it, loaded when chosen;
- FlexarrKit and Android `core` carry the packs as resources, scoped to the strings that app uses;
- the server embeds them;
- for the text the operating system reads itself, `make strings` also writes the native files: Apple's
  `InfoPlist.xcstrings` (permission prompts) and `AppShortcuts.xcstrings` (Siri phrases), Android's
  `values-<code>/strings.xml` (the app name, widget and shortcut labels) and `locales_config.xml`. These are what
  make the App Store and Google Play list the languages.

**Downloaded.** Between releases, a merge to main runs `make languages`, which writes `site/languages/index.json`
and the published packs, and Vercel serves them at flexarr.io/languages:

```json
{
  "format": 1,
  "languages": [
    { "code": "de", "name": "Deutsch", "direction": "ltr", "version": 20261103160000,
      "sha256": "…", "size": 412345, "coverage": { "web": 0.82, "apple": 0.75, "android": 0.75, "server": 0.4 } }
  ]
}
```

A household's Flexarr server reads that index (`Languages:IndexUrl`, flexarr.io's by default; blank turns downloads
off) at most once a day. It downloads a pack the first time a profile asks for that language, checks its size and
hash, validates every message, keeps it in `<data>/languages/`, and refreshes it when the index has a newer version.
Offline, it keeps using what it has. The apps only ever talk to their own server:

- `GET /api/v1/languages`: the languages on offer (built in or in the index), with native names, directions,
  coverage and versions;
- `GET /api/v1/languages/{code}?scope=web|apple|android|server`: the newest pack the server has for that language,
  only the strings that scope uses, with an ETag.

Both need no sign-in: they're public text, and the screens before sign-in need them.

**Which copy wins.** An app compares its built-in pack with the server's by version and uses the newer. Strings the
newer one lacks come from the older, then from English.

## Choosing a language

- Each profile has a **Language**: unset (*Device language*) or a code. It's on `/me` and in the profile's
  preferences, and kids can change theirs like any other profile setting.
- Before sign-in, and on a profile that hasn't chosen, the apps use the device's languages, best match first
  (`pt-BR` falls back to `pt`).
- The apps send the language they're showing as `Accept-Language`. The server answers in the signed-in profile's
  language, else `Accept-Language`, else English. Push notifications use the profile's stored language, then the
  language of the device that registered.
- The Language row in each app's settings appears only when a language other than English is available, so with no
  packs nothing changes.
- Dates, times, numbers and plurals follow the language shown, not the device's own setting.

## Right to left

Each language has a direction. The apps set it (`<html dir>`, SwiftUI's `layoutDirection`, Compose's
`LocalLayoutDirection`), and the web's styles use logical properties (`margin-inline-start`, not `margin-left`).
Mirroring the player, scrubber and queue waits until a right-to-left pack exists.

## The server's own words

Error messages, push notifications and the setup guide go through `Loc.T`. Errors also carry a stable `code` (the
English message's key) and its `args`, so apps can tell errors apart without matching words, and in English the
`detail` is exactly what it was. Anything the server caches that holds translated text is kept per language
(`Vary: Accept-Language`).

Staying English: the changelog and release notes, the MCP tools' text (read by AI), and the logs.

## Testing

`make strings pseudo=1` writes `languages/packs/qps.json` (git-ignored), a pseudo-language made from English: accented (*Ḿàŕķ àś
ŵàţçĥéḍ*), 30% longer, and wrapped in ⟦ ⟧. It's never published or built into a release. Point a preview at it,
and any plain English on screen is a string that hasn't been moved, and any clipping is a layout too tight for
German.

## Adding a language

1. Add it to `languages/languages.json` with its native name and direction.
2. Copy `packs/en.json` to `packs/<code>.json` (or start empty) and translate. Leave out what isn't translated yet.
3. `make strings`, then open a pull request. It's reviewed like code: this text is shown in people's homes.
4. On merge, flexarr.io publishes it; servers pick it up within a day, and the next app releases build it in.
