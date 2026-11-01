# Flexarr design language

## Subject

A personal cinema: your own films, shows and music, watched in a dark room on a laptop or across the room on a TV. The job of the interface is to get out of the way of the artwork and get you playing something fast.

## The one bold idea: the room takes the colour of what you're looking at

Like ambient TV backlighting, every title carries an **ambient colour** extracted from its own artwork (computed server-side when artwork is cached). Detail pages, the home hero and the player wash the dark room in that colour; focus rings and primary buttons pick it up. The chrome itself stays quiet and neutral so the posters are the only loud thing on screen.

## Tokens

| Token | Value | Use |
| --- | --- | --- |
| `--room` | `#1B1720` | Base background: a dim, plum-tinted screening room (deliberately not near-black) |
| `--seat` | `#25202C` | Raised surfaces: panels, menus, inputs |
| `--aisle` | `#3A3343` | Hairlines and inactive controls |
| `--screen` | `#F2EEE7` | Primary text: warm projected white |
| `--usher` | `#A39AAE` | Secondary text |
| `--silver` | `#A9C2EC` | Fallback accent (silver screen) when a title has no ambient colour |
| `--go` | `#7FD1A0` | Connected, on, set up: status dots, switches that are on, Connect buttons |
| `--danger` | `#F08A7E` | Destructive actions (delete, remove, sign out everywhere) and error messages, never decoration |
| `--record` | `#E5484D` | The live and recording dot on Live TV, and nothing else: the one red that isn't for deleting |
| `--well` | `#141118` | Inside text fields and menus, below the panel, with a light hairline border |
| `--ambient` | per title | Set inline from artwork; drives glows, focus and primary buttons |

**Type**
- *Big Shoulders Display* (variable): titles, row headings, Top 10 numerals. Tall and condensed like a cinema marquee, so long titles fit on posters and in heroes.
- *Atkinson Hyperlegible Next* (variable): everything else. Designed for legibility, which matters from a sofa three metres from a TV.
- Scale (1.25 ratio from 17px body): 13.6 / 17 / 21 / 26.5 / 33 / 41.5 / 52 / 65 / 81px. Display headings use weight 700–800 and tight tracking (-0.01em); body runs at 1.5 line height and stays under ~70 characters per line.

**Shape**
- Posters and stills: 4px radius, like a printed one-sheet.
- Buttons and inputs: 10px.
- Panels: 14px.
- Pills (chips, season pickers) and circles (round icon buttons, avatars) are the only other shapes.
- There are no drop shadows on cards. Elevation comes from ambient glow on focus and hover only.

## Layout

- **Sidebar navigation** on the left: the wordmark and Home, then the libraries, then Collections, Discover and Coming soon. It holds any number of libraries, scrolling on its own, and collapses to an icon rail. On phones it is a drawer behind a menu button. Search (⌘K), settings and the profile float at the top right of the page in a bar that is transparent over heroes and solid once you scroll.
- **Home:** a full-bleed hero (backdrop, clear logo, one line of context, Play and More info buttons), then horizontal rows. The *Top 10* row uses giant outlined marquee numerals beside each poster, the one place where numbering is meaningful.
- **Library:** a poster grid with sort and filter controls and an A–Z rail for jumping through large collections.
- **Detail:** backdrop wash in ambient colour, logo or title, facts, overview, Play/Resume, Trailer, cast, then seasons as tabs with episode stills.
- **Player:** chrome-less until the pointer moves; a scrubber with frame previews; skip intro/credits as a single floating action; up next counts down in the corner.

```
┌───────────────────────────────────────────────────────────────┐
│ flexarr   Home  Movies  TV  Kids        ⌘K Search   ⚙     │  ← transparent over hero
│                                                               │
│   [CLEAR LOGO]                                                │
│   2016   1h 56m   Crime, Comedy                               │
│   One-line overview…                                          │
│   [▶ Play]  [More info]                                        │
│░░░░░░░░░░░░░ ambient colour fades into the room ░░░░░░░░░░░░░░│
│ Continue watching                                             │
│ [▭▭▭▭][▭▭▭▭][▭▭▭▭][▭▭▭▭]                                         │
│ Top 10 in your library                                        │
│ 𝟏[▯] 𝟐[▯] 𝟑[▯] 𝟒[▯] …                                          │
└───────────────────────────────────────────────────────────────┘
```

Content is left-aligned throughout and shares one 4vw left edge, so heroes, rows and grids line up.

## Principles

1. Artwork first. Text never sits on top of a poster; titles go below or in heroes, over a scrim.
2. Everything is reachable by keyboard and arrow keys, with large visible focus (TV remotes and keyboard users get the same experience).
3. Motion only answers an action: focus glow, a hero crossfade, a row scroll. There are no entrance animations, and `prefers-reduced-motion` is respected.
4. Plain words: "Play", "Resume from 32:10", "Skip intro". Failures say what happened and what to do.
5. Colour means something in controls: the ambient/silver fill is the one main action, green sets something up or is on, coral-red deletes, and outlined buttons are everything else. A row's own icon buttons stay quiet until pointed at.
6. Avoid these: amber or gold accents (Plex), red as an accent (Netflix; red is only for deleting, plus the `--record` dot), all-caps labels, eyebrow text, middle-dot meta strings and arrow-suffixed links.

## Tokens in code

The colours, radii and type scale live in one file, `design/tokens.json`. `make tokens` writes them out for every client: `frontend/src/styles/tokens.generated.css`, `clients/apple/Shared/Design/Tokens.swift` and `clients/android/app/src/main/kotlin/io/github/flexarr/ui/theme/Tokens.kt`. CI fails if they're out of date. Change a value there, never in a client.

## Platforms

Flexarr looks like one product everywhere, and behaves like it belongs on each device. What's shared and what's native:

**Shared on every client**
- The tokens above, the ambient colour, and the 4 / 10 / 14 shapes.
- Big Shoulders Display for titles, row headings and Top 10 numerals.
- The main button's look: the ambient fill (silver when a title has none). On the TVs it's ambient at rest and projected white when focused, as tvOS and Google TV expect focus to look.
- The film and show page's actions, in this order: **Play** or **Resume from 32:10**, **Start over** (only once there's progress), **Shuffle** (shows), **Trailer** (not for children), **Watched**, **Watchlist**, **Download** (phones only), then **⋯**. The ⋯ menu holds the rest: subtitles, add to a collection, fix match, allow or block for kids, delete. The order is defined once in `DetailActions` (FlexarrKit and Android `core`) and on web in `ItemPage`.
- The words (below).

**Native on each**

| | Web and Mac | iPhone | Apple TV | Android phone | Android TV |
| --- | --- | --- | --- | --- | --- |
| Navigation | Sidebar, drawer on small screens | Tab bar (sidebar on iPad) | Sidebar | Bottom bar (rail on tablets) | Sidebar |
| Body font | Atkinson Hyperlegible Next | SF, with Dynamic Type | SF | Roboto | Roboto |
| Focus | Ambient glow | Press dims | System card lift plus ambient glow | Press dims | Scale plus ambient glow |
| Back | Browser | Swipe from the edge | Menu button | Predictive back | Back button |
| Notices | Toast at the bottom | Banner at the top | Alert | Snackbar | Snackbar |
| Trailer | Embedded player | Sheet with the embedded player | YouTube app, when installed | Sheet with the embedded player | YouTube app, when installed |
| Player | Custom controls | Custom controls, AirPlay, PiP | Custom controls | Custom controls, PiP | Custom controls |

## Words

The same words on every client and in the guide:

- **Movies** (never "Films"), **shows**, **episodes**. Library names people chose themselves are theirs.
- **Play**, **Resume from 32:10**, **Resume S1E3**, **Start over** (never "Play from beginning" or "Play from the start").
- **Skip intro**, **Skip recap**, **Next episode in 5**: sentence case, like every label.
- **Mark as watched** and **Mark as unwatched** for video, **Mark as played** for podcasts, **Mark as finished** and **Mark as unread** for books. **Mark season as watched**.
- **Watchlist**, **Continue watching**, **Up next**, **Coming soon**, **Discover**, **Cast and crew**, **More like this**.
- Episodes: **S1E3** where space is short, **Season 1, Episode 3** in headings. No zero padding, no pipes.
- Facts sit side by side with space between them, never joined with middle dots.

## What each client has

What's on each client today. The gaps are for future briefs.

| Screen | Web | iPhone | Apple TV | Android phone | Android TV |
| --- | --- | --- | --- | --- | --- |
| Home, libraries, detail, search, player | ✅ | ✅ | ✅ | ✅ | ✅ |
| Trailer | ✅ | ✅ | ✅ | ✅ | ✅ |
| Coming soon page | ✅ | ✅ | ✅ | ✅ | ✅ |
| Watchlist | ✅ | ✅ | ✅ | ✅ | ✅ |
| Music | ✅ | ✅ | ✅ | ✅ | ✅ |
| Audiobooks | ✅ | ✅ | ✅ | ✅ | 🟡 points to the phone |
| Podcasts | ✅ | ✅ | ❌ | ✅ | ❌ by design |
| Live TV | ✅ | ✅ | ✅ | ✅ | ✅ |
| Discover | ✅ | ❌ | ✅ | ❌ | ✅ |
| Collections | ✅ | ❌ | ❌ | ❌ | ❌ |
| Playlists and mixes pages | ✅ | 🟡 in Music | 🟡 in Music | 🟡 in Music | 🟡 in Music |
| Downloads | ❌ | ✅ | ❌ | ✅ | ❌ |
| A–Z rail | ✅ | ❌ | ❌ | ❌ | ✅ |
| Server settings (libraries, backups, logs…) | ✅ | ❌ | ❌ | ❌ | ❌ by design |

✅ there, 🟡 partly, ❌ missing.
