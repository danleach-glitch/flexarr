# Mac app (the player): plan

A native Mac app, **Flexarr**, that browses and plays from a Flexarr server the way the iPad app does. It is a separate app from **Flexarr Server** (`clients/macos`), the menu bar app that *is* a server: a MacBook Air runs the player and streams from a Mac Mini running the server, and one Mac can run both. Checklist: the *Mac Player Brief* page on the checklist hub.

Decided:

- **Native macOS SwiftUI**, a new `FlexarrMac` target in `clients/apple`. Not Mac Catalyst (AetherEngine's FFmpeg and the tunnel have no Catalyst builds) and not the web app in a window (it would play only what browsers can, so more transcoding).
- **Universal purchase** with the iPhone and Apple TV app: one *Flexarr* listing (app 6826618084), uploaded under the iPhone app's bundle ID as the TV app is. Flexarr Server keeps its own listing. Apple doesn't let an app leave a universal purchase later.
- **Apple silicon only**, macOS 15 Sequoia or later (FlexarrKit's and AetherEngine's minimum).
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

## Phases

1. **Plan and server:** this page, `DeviceType.Mac`, the web's icon and name for it.
2. **Foundations:** the target, a platform image type, re-gating, the data-protection keychain, the tunnel slice, error reports and the device header.
3. **The app:** shell and sidebar; connect, profiles and PIN; Home to requests; music; books and podcasts; Live TV; Settings; menus and shortcuts.
4. **The player:** AppKit player, tracks and subtitles, full screen and media keys, Picture in Picture, AirPlay, Live TV.
5. **Downloads and notifications.**
6. **Shipping:** icon and privacy manifest, `make mac-player-install` and `make mac-player-upload`, CI, parity tour, design spec, guide, apps page, changelog, store notes.

## Risks

- If the iPhone app is offered on Apple silicon Macs as "Designed for iPad" today, the native app replaces it on the same listing. Check before the first upload.
- The player is the largest piece; AetherEngine supports macOS 15 but hasn't run inside Flexarr there yet. HDR on Mac displays and engine Picture in Picture may need engine changes; AVPlayer is the fallback.
- Background downloads in the sandbox and APNs on macOS are untried here.

## Later

Flexarr Server opening the player when it's installed; Handoff; the Apple TV remote; rich notifications; Intel Macs; a mini player in the menu bar.
