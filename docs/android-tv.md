# Android TV

The Android app (`clients/android`) on Google TV and Android TV: the same app and the same APK as the phone's, with a
TV interface of its own. Fire TV isn't planned for now (only older Fire TVs run Android; newer ones run Vega OS).

## Decisions

- **One app.** The phone app's application ID, `:core`, `AppModel`, players, logging and sign-in carry over. When the
  app runs on a TV (`UI_MODE_TYPE_TELEVISION`, already reported to the server as `AndroidTv`), `MainActivity` shows
  the TV interface instead of the phone's.
- **Match the Apple TV app.** Every TV screen is a port of its `clients/apple/FlexarrTV` counterpart: layout,
  wording, sizes and behaviour. Read the Swift before building a screen.
- **Compose for TV** (`androidx.tv`), in `ui/tv/`. Phone screens are built for touch and aren't reused for TV.
- **Google TV and Android TV only**, through Google Play.
- **The first release** includes films and TV shows, Live TV, music (no podcasts), the Google TV home-screen rows,
  refresh-rate matching and audio passthrough.

## Stages

Each stage is a pull request. Main stays releasable: until stage 5, the TV interface turns on only in debug builds, and
the manifest doesn't offer the app to TVs yet.

1. **Something you can watch.** The manifest changes (touchscreen and Android TV support both optional, a TV launcher
   entry, a 320×180 banner, landscape on TV), the sidebar, code sign-in with a QR code (`ConnectView`), "Who's
   watching?", Home (hero and rows), the title page, and a player for the remote: left/right to seek with trickplay
   previews, menus in a side panel, Back, and the remote's media buttons.
2. **Films and TV in full.** Library grids, genres and browsing; Search with the on-screen keyboard and voice;
   Discover; Settings (playback, home screen, libraries, sidebar order, profile, server, diagnostics).
3. **Live TV and music.** The guide grid and guide overlay, live player, recordings; the music library, artist, album
   and playlist pages, Now Playing with the aurora, and the Now Playing sidebar entry.
4. **Using the TV well.**
   - Continue Watching in the Google TV home screen's Watch Next row, plus a Flexarr channel on the home screen (the
     equivalent of the Apple TV's Top Shelf).
   - The TV switches to the film's frame rate (Android 11 and later; a setting, as on Apple TV).
   - Audio passthrough (AC-3, E-AC-3 with Atmos, DTS, TrueHD) when the TV or amplifier takes it.
   - `DeviceCapabilities` reports the TV's real HDR10, HDR10+, Dolby Vision and 4K support, so more titles play
     without converting.
5. **Release.** Offer the app to TVs in the manifest, opt in to TV in Play Console, add TV screenshots and the banner
   to the listing (`app/src/main/play`), get through Google's TV review, and move Android TV on flexarr.io/apps to
   available. Release builds must still refuse sign-in from launch arguments (`LAUNCH_ARGUMENTS` off) once the TV
   entries move into the main manifest, since any app on a shared TV could send them.

## Left out on TV

Downloads, widgets, Android Auto, shake for sleep, the Chromecast button, picture in picture, the review prompt,
podcasts, push notifications, and the portrait lock.

## Testing

- The `Flexarr_Google_TV` emulator is for day-to-day work: drive it with `adb shell input keyevent` (D-pad).
- A real Google TV stick is for what the emulator can't show: HDR, Dolby Vision, passthrough, refresh-rate switching,
  and speed on 2 GB of RAM.
- Measure speed with `benchmarkRelease` (`./gradlew :app:installBenchmarkRelease`): release as it ships, with R8, but
  profileable and with the TV interface on. Debug builds run Compose several times slower, so they say little about
  speed. `BaselineProfileGenerator.generateTv` records the TV journey for the baseline profile on a TV.
