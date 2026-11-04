# Mac app (the player): plan

A native Mac app, **Flexarr**, that browses and plays from a Flexarr server the way the iPad app does. It is a separate app from **Flexarr Server** (`clients/macos`), the menu bar app that *is* a server: a MacBook Air runs the player and streams from a Mac Mini running the server, and one Mac can run both. Checklist: the *Mac Player Brief* page on the checklist hub.

Decided:

- **Native macOS SwiftUI**, a new `FlexarrMac` target in `clients/apple`. Not Mac Catalyst (AetherEngine's FFmpeg and the tunnel have no Catalyst builds) and not the web app in a window (it would play only what browsers can, so more transcoding).
- **Universal purchase** with the iPhone and Apple TV app: one *Flexarr* listing (app 6826618084), uploaded under the iPhone app's bundle ID as the TV app is. Flexarr Server keeps its own listing. Apple doesn't let an app leave a universal purchase later.
- **Apple silicon only**, macOS 15.2 or later (the iPhone app's iOS 18.2, for its Siri intents; FlexarrKit and AetherEngine need 15).
- **Everything the iPad has**: movies, TV, music, audiobooks, podcasts, Live TV, search, watchlist, requests, profiles and kids, settings. Not CarPlay, widgets, Live Activities, the Apple TV remote or Shake to extend.
- **Downloads and offline mode** in the first release, for laptops away from home.
- **Video plays in the main window**, which becomes the player. Esc or Back returns to the page; ⌃⌘F is full screen.
- **Also:** an AirPlay button, Picture in Picture, family guests (the tunnel built for macOS), new-episode notifications, and the Mac in the parity tour.

## What's shared and what's the Mac's own

| Part | Where | Notes |
| --- | --- | --- |
| API, models, words, design tokens | FlexarrKit, `Shared/Design` | Already build for macOS 15 |
| App model, playback session, Next Episode, music and book players | `Shared/App`, `Shared/Playback`, `Shared/Music`, `Shared/Books` | Re-gated: `#else` branches that meant iOS say `os(iOS)`; audio session and window presentation sit behind a small platform layer |
| Screens | iPad screens move from `FlexarriOS/Screens` into `Shared/Screens` as they're made to build on both | The Mac counts as a regular size class |
| Navigation | `TabView` with `.sidebarAdaptable` | A native sidebar on macOS 15 |
| Player | `FlexarrMac/Player` | New AppKit view controller on the same `PlaybackController` and transports (AetherEngine direct play, AVPlayer for everything else) |
| Menus, shortcuts, Settings window | `FlexarrMac/App` | Space, ←/→, ↑/↓, ⌘F, ⌘[, ⌘1…, ⌘, |
| Downloads | `DownloadStore`, moved into Shared | Files in the app's sandbox container |
| Notifications | `PushNotifications`, moved into Shared | Same bundle ID, so the same APNs topic: the relay needs no change. Plain alerts; the iPhone's poster image waits |
| Family guests | `clients/tunnel` gains a `macos` slice | `GOOS=darwin`, arm64 |

The server learns one thing: `X-Flexarr-Device: Mac`, a new `DeviceType.Mac`, so Sign-ins, pairing and family invites name and draw it. Older servers ignore a value they don't know.

## The Mac's page layout

The Mac app shares the iPad's screens, so its look comes from a few rules applied once, not screen by screen. Colours,
radii, icons and words are the design system's (`design/tokens.json`, `docs/design.md`); these are the Mac's sizes and
places. Every screen follows them; a screen that needs to break one says why.

**Type** (`FlexarrMac/App/MacTypeScale.swift`). The shared screens name iOS text styles, which macOS sets two sizes
smaller. The Mac app replaces them with a scale close to the iPad's: body 15, callout and subheadline 14, footnote 13,
caption 12, caption 2 11, headline 16 semibold, title 3 19, title 2 22, title 28, large title 34. Titles of pages and
rows stay in the marquee face (Big Shoulders Display). Text with no style of its own is body. Controls are large.

**Margins and spacing** (`Layout`, Mac values). One page margin, 32 pt, for heroes, row headings, rows, grids and
lists. Posters 176 pt wide with 18 pt between them. Rows 28 pt apart.

**The window's toolbar** (`Shared/Design/PageChrome.swift`). See-through, over the page: heroes run up under it.
- Left: back, and nothing else: `PageBackButton`, a capsule the tabs' height (34 pt), in from the corner.
- Middle: the page's tabs (Browse, All, Genres…; Discover's Movies and shows or Music; Live TV's Channels, Guide and
  Recordings), from `pageSwitch`: `PageTabs`, a capsule track with the chosen tab filled to its edges, the same size
  and place on every page.
- Right: the page's actions (Filter, Sort and filter, Recordings, Guide), from `pageActions`, each a named button with
  its icon; search, where a page has one, after them (`pageSearch`: `PageSearchField`, the tabs' height; a library's
  searches its titles). Search's own page has its field in the middle. Controls that
  belong to part of a page (a podcast's episode order, beside Unplayed and All) stay on the page, by that part.

**Lists** (Settings, Downloads; `macReadable`). One centred column, 760 pt at most, with the page margin either side,
headed by the page's title in the marquee face ("Settings › Server" inside Settings), rows at least 46 pt tall, icons
14 pt from their words, and no toolbar band (back floats over the page). Every Settings page goes through
`settingsPage`. No scroll bars anywhere: the Mac app is scrolled with a trackpad or wheel.

**Wide pages.** Title pages put Play (or Request) at its own width with the other actions beside it; episodes are wide
rows with the overview beside the picture; Coming soon's days are grids of cards; downloads have big artwork.

**The player.** One margin, 36 pt, for the close button, the title, the timeline and the controls. The top row sits
below the window's own buttons (in full screen, near the top). The bottom: the timeline, its times under its ends, then
the facts on the left, play (a 68 pt glass circle) and the skips in the middle, and the menus (audio, subtitles, speed,
sleep timer, AirPlay, full screen) on the right, every target at least 44 pt, lighting up under the pointer.

**Who's watching?** The whole window, centred, with a Cancel button under the profiles; right-click a kids profile for
child lock. **Splash:** about a second of the mark and name, fixed in place as it fades.

## Phases

1. **Plan and server:** this page, `DeviceType.Mac`, the web's icon and name for it.
2. **Foundations:** the target, a platform image type, re-gating, the data-protection keychain, the tunnel slice, error reports and the device header.
3. **The app:** shell and sidebar; connect, profiles and PIN; Home to requests; music; books and podcasts; Live TV; Settings; menus and shortcuts.
4. **The player:** AppKit player, tracks and subtitles, full screen and media keys, Picture in Picture, AirPlay, Live TV.
5. **Downloads and notifications.**
6. **Shipping:** icon and privacy manifest, `make mac-player-install` and `make mac-player-upload`, CI, parity tour, design spec, guide, apps page, changelog, store notes.

## Notes for whoever works on it

- **Listening.** AetherEngine's HLS server (the player's direct play) listens on every interface, for AirPlay, behind a
  random token per playback; hence the network-server entitlement. The guest tunnel's local proxy (`clients/tunnel`) is
  on 127.0.0.1, which on a Mac other programs of the same user can reach; the server still asks for the sign-in.
- **Keychain.** A store build keeps the sign-in in the data-protection Keychain; a home build signed with a
  development certificate and no provisioning profile can't, and uses the file Keychain. Switching between the two
  kinds of build signs you out.

## Risks

- If the iPhone app is offered on Apple silicon Macs as "Designed for iPad" today, the native app replaces it on the same listing. Check before the first upload.
- The player is the largest piece; AetherEngine supports macOS 15 but hasn't run inside Flexarr there yet. HDR on Mac displays and engine Picture in Picture may need engine changes; AVPlayer is the fallback.
- Background downloads in the sandbox and APNs on macOS are untried here.

## Later

Flexarr Server opening the player when it's installed; Handoff; the Apple TV remote; rich notifications; Intel Macs; a mini player in the menu bar.
