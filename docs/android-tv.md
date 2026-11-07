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

Each stage is a pull request. Stages 1–3 shipped in debug builds only; since the Google Play closed test (stage 5,
brought forward) every build has the TV interface and the manifest offers the app to TVs. Stage 4 (built November 2026, on the
`google-play` branch) lands before production.

1. **Something you can watch.** The manifest changes (touchscreen and Android TV support both optional, a TV launcher
   entry, a 320×180 banner, landscape on TV), the sidebar, code sign-in with a QR code (`ConnectView`), "Who's
   watching?", Home (hero and rows), the title page, and a player for the remote: left/right to seek with trickplay
   previews, menus in a side panel, Back, and the remote's media buttons.
2. **Films and TV in full.** Library grids, genres and browsing; Search with the on-screen keyboard and voice;
   Discover; Settings (playback, home screen, libraries, sidebar order, profile, server, diagnostics).
3. **Live TV and music.** The guide grid and guide overlay, live player, recordings; the music library, artist, album
   and playlist pages, Now Playing with the aurora, and the Now Playing sidebar entry.
4. **Using the TV well.** Built 7 November 2026; proving passthrough and HDR waits for a real TV and amplifier.
   - Continue Watching in the Google TV home screen's Watch Next row, plus a Flexarr channel on the home screen (the
     equivalent of the Apple TV's Top Shelf).
   - The TV switches to the film's frame rate (Android 11 and later; a setting, as on Apple TV).
   - Audio passthrough (AC-3, E-AC-3 with Atmos, DTS, TrueHD) when the TV or amplifier takes it.
   - `DeviceCapabilities` reports the TV's real HDR10, HDR10+, Dolby Vision and 4K support, so more titles play
     without converting.

   How (decided 7 November 2026, with no server change and no new API field):
   - **Watch Next only.** Google TV ignores apps' preview channels and shows only the Watch Next row, so there's
     no Flexarr channel for now. The row holds the last profile's Continue Watching (films and episodes from
     `home/continue`, with progress), the next episode of a show (as *Next*), then recently played albums. It's
     cleared on sign-out, for family guests, and while the profile has a PIN, since anyone at the TV sees it. It
     refreshes when playback stops, the profile changes, the app opens or comes back online, and every 6 hours.
     A card resumes playback through a launch token of its own (never `AppLink`'s in-app secret). Removing a card
     on the home screen calls `DELETE /home/continue/{id}` (servers 0.51 on).
   - **Frame rate follows the TV's own setting** (*Match content frame rate*), as the Apple TV follows Match
     Content: the player asks for the film's frame rate before its first frame and the TV decides. No Flexarr
     setting.
   - **Passthrough is *Lossless surround*** (Settings › Playback › This TV, the Apple TV's wording), on by default
     when the HDMI output takes a format. The profile lists `dts` and `truehd` only for formats the output takes,
     and the server then plays those files directly (HLS can't carry them). Dialogue boost, Night mode and Speakers
     work on decoded sound, so while one is on surround is decoded, as on the Apple TV.
   - **Real capabilities:** HDR types from the display, Dolby Vision only with its decoder, 4K from the decoders
     rather than the screen (a 1080p TV scales a 4K file itself). HDR10+ needs no profile field: it rides on HDR10.
     Settings › Diagnostics shows all of it, since the emulator has no HDR screen or amplifier.
5. **Release.** Done ahead of stage 4, for Google Play's closed test: the manifest offers the app to TVs, the TV
   screenshots and banner are in the listing (`app/src/main/play`), and release builds still refuse sign-in from
   launch arguments (`LAUNCH_ARGUMENTS` off), since any app on a shared TV could send them. Still to come: opt in to
   TV in Play Console, Google's TV review, and Android TV on flexarr.io/apps once it reaches production.

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
