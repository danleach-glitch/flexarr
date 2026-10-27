# Roadmap

What's built, what's next and in what order. Each planned item links to its discussion under [Ideas](https://github.com/danleach-glitch/flexarr/discussions/categories/ideas), where the detail, scope and votes live. What has shipped is in the [changelog](../CHANGELOG.md).

Sizes are rough: **S** a day or two, **M** about a week, **L** several weeks.

## Done

- **Foundations:** server, API-key auth, CI (server, web, Apple, Docker, CodeQL), Docker image, macOS deploy, semver releases
- **Library:** scanner for films, TV and music, file name parsing, ffprobe media info, webhook-driven and scheduled rescans, Fix match, renaming libraries, delete through Sonarr and Radarr, library health, Plex history import
- **Metadata:** from Sonarr, Radarr and Lidarr with no keys of our own, TVmaze cast, image cache, music artwork and album kinds
- **Playback:** direct play on the device (AV1, VC-1 and the rest), repackaging and hardware transcoding, HDR10, HLG and Dolby Vision 5, 7 and 8.1, Atmos passthrough and lossless surround, subtitles with timing and size, skip intro, recap and credits, Up Next, scrubbing previews
- **Finding things:** Home rows you arrange, search, person and genre pages, collections (manual, smart and Radarr series), Coming soon, Discover and Seerr requests, shuffle
- **Music:** queue, playlists, synced lyrics, daily mixes, artist radio, near-gapless, crossfade, volume normalisation, offline albums
- **Podcasts:** Apple's directory and any feed, following per profile, Up Next across devices, speeds and sleep timer, OPML, offline and automatic downloads, widgets, CarPlay (awaiting Apple)
- **Live TV:** channels and guide from Dispatcharr, On now and Guide on every client, rewind and Jump to live, favourites, surround sound, a reduced stream away from home, one provider connection per channel shared by everyone on it
- **Recording:** programmes and series from the guide, recorded as broadcast, a Recordings library, clash and free-space checks, recordings ahead of live viewing
- **Apple apps:** Apple TV, iPhone and iPad with Flexarr's own player, profiles, a sidebar, Discover and requests on the Apple TV, Top Shelf with posters or wide artwork, downloads, Picture in Picture and AirPlay
- **Notifications:** new films, shows, episodes, podcasts and recordings, requests arriving, and a morning list of what's on, through an end-to-end encrypted relay
- **Control:** Siri and Shortcuts (play, shuffle, on another device), play on another device, an MCP server for assistants, [Home Assistant](https://github.com/danleach-glitch/flexarr-home-assistant) (media players and playback triggers)
- **Profiles and access:** per-person history and settings, PINs, a sign-in per device, Bonjour and code pairing, Tailscale with automatic fallback
- **Looking after the server:** nightly backups and restore, settings export and import, logs in Settings with diagnostics, opt-in error reports

## Next

In order. Small items first, so each stage ships something.

1. **Rounding out playback** (S each)
   - Subtitles: teletext, closed captions and height; ASS styling after (M) — [#40](https://github.com/danleach-glitch/flexarr/discussions/40)
   - HDR10+ — [#41](https://github.com/danleach-glitch/flexarr/discussions/41)
   - The Apple TV app on the App Store, after the iPhone app's review
2. **Watchlist** (S–M) — [#39](https://github.com/danleach-glitch/flexarr/discussions/39)
3. **Multiview** (M) — [#38](https://github.com/danleach-glitch/flexarr/discussions/38)
4. **While you watch** (M each)
   - Cast and trivia — [#43](https://github.com/danleach-glitch/flexarr/discussions/43)
   - Family filter, with parental ratings for profiles — [#44](https://github.com/danleach-glitch/flexarr/discussions/44)
   - TrueHD Atmos with height channels — [#45](https://github.com/danleach-glitch/flexarr/discussions/45)
5. **Music, part three**
   - A visualiser on Now Playing (S–M) — [#46](https://github.com/danleach-glitch/flexarr/discussions/46)
   - Mixes and radio by how songs sound (L) — [#47](https://github.com/danleach-glitch/flexarr/discussions/47)

## Later

- Photo libraries — [#48](https://github.com/danleach-glitch/flexarr/discussions/48)
- Android, Android TV and Chromecast — [#49](https://github.com/danleach-glitch/flexarr/discussions/49)
- PostgreSQL as an alternative to SQLite — [#14](https://github.com/danleach-glitch/flexarr/issues/14)
- Watching libraries the \*arr apps don't manage — [#3](https://github.com/danleach-glitch/flexarr/issues/3)

## Other ideas

Open for discussion, not yet scheduled:

- **Watching:** watching together [#20](https://github.com/danleach-glitch/flexarr/discussions/20), a "Tonight" picker [#21](https://github.com/danleach-glitch/flexarr/discussions/21), dialogue boost and night mode [#23](https://github.com/danleach-glitch/flexarr/discussions/23), search inside what people say [#24](https://github.com/danleach-glitch/flexarr/discussions/24), skipping more than intros [#25](https://github.com/danleach-glitch/flexarr/discussions/25), getting films ready for a trip [#26](https://github.com/danleach-glitch/flexarr/discussions/26), your year in review [#29](https://github.com/danleach-glitch/flexarr/discussions/29)
- **Library:** a storage manager with Sonarr and Radarr [#22](https://github.com/danleach-glitch/flexarr/discussions/22)
- **Podcasts and music:** chapters [#30](https://github.com/danleach-glitch/flexarr/discussions/30), skipping intros and outros [#31](https://github.com/danleach-glitch/flexarr/discussions/31), transcripts [#32](https://github.com/danleach-glitch/flexarr/discussions/32), voice boost and silence trimming [#33](https://github.com/danleach-glitch/flexarr/discussions/33), AirPlay from the web [#34](https://github.com/danleach-glitch/flexarr/discussions/34), podcasts on Apple TV [#35](https://github.com/danleach-glitch/flexarr/discussions/35)
