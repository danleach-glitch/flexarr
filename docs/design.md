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
| `--ambient` | per title | Set inline from artwork; drives glows, focus and primary buttons |

**Type**
- *Big Shoulders Display* (variable): titles, row headings, Top 10 numerals. Tall and condensed like a cinema marquee, so long titles fit on posters and in heroes.
- *Atkinson Hyperlegible Next* (variable): everything else. Designed for legibility, which matters from a sofa three metres from a TV.
- Scale (1.25 ratio from 17px body): 13.6 / 17 / 21 / 26.5 / 33 / 41.5 / 52 / 65 / 81px. Display headings use weight 700–800 and tight tracking (-0.01em); body runs at 1.5 line height and stays under ~70 characters per line.

**Shape**
- Posters and stills: 4px radius, like a printed one-sheet.
- Buttons and inputs: 10px.
- Panels: 14px.
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
5. Avoid these: amber accents (Plex), red (Netflix), all-caps labels, eyebrow text, middle-dot meta strings and arrow-suffixed links.
