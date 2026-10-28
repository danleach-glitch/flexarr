# Roadmap

What's built, what's next and in what order. Each planned item links to its discussion under [Ideas](https://github.com/danleach-glitch/flexarr/discussions/categories/ideas), where the detail, scope and votes live. What has shipped is in the [changelog](../CHANGELOG.md).

Sizes are rough: **S** a day or two, **M** about a week, **L** several weeks.

## Done

- **Foundations:** server, API-key auth, CI (server, web, Apple, Docker, CodeQL), Docker image with PUID/PGID and VA-API/Quick Sync, macOS deploy, semver releases, Settings › Updates with one-click updates on a Mac, backups, logs and opt-in error reports
- **Library:** scanner for films, TV and music, file name parsing, ffprobe media info, webhook-driven and scheduled rescans, Fix match, delete through Sonarr and Radarr, library health, Plex history import, media folders that find themselves, libraries without the \*arr apps, Home videos
- **Metadata:** from Sonarr, Radarr and Lidarr with no keys of our own, local `.nfo` files and artwork, TVmaze, TMDB with your own key, image cache, music artwork and album kinds
- **Playback:** direct play on the device (AV1, VC-1 and the rest), repackaging and hardware transcoding, HDR10, HLG and Dolby Vision 5, 7 and 8.1, Atmos passthrough and lossless surround, subtitles with timing, size and the system caption style, skip intro, recap and credits, Up Next, scrubbing previews
- **Finding things:** Home rows you arrange, libraries of the same kind combined on Home, search, person and genre pages, collections (manual, smart and Radarr series), Coming soon, Discover and Seerr requests, shuffle
- **Music:** queue, playlists, synced lyrics, daily mixes, artist radio, near-gapless, crossfade, volume normalisation, offline albums
- **Podcasts:** Apple's directory and any feed, following per profile, Up Next across devices, speeds and sleep timer, OPML, offline and automatic downloads, notifications, widgets, CarPlay (awaiting Apple)
- **Live TV:** Dispatcharr sources, guide, favourites, pause and rewind, recording and series recording
- **Apple apps:** Apple TV, iPhone and iPad with Flexarr's own player, profiles, Top Shelf with title logos, downloads, Picture in Picture and AirPlay; the iPhone and iPad app on the App Store
- **Control:** Siri and Shortcuts (play, shuffle, on another device), play on another device, an MCP server for assistants, [Home Assistant](https://github.com/danleach-glitch/flexarr-home-assistant) (media players and playback triggers)
- **Profiles and access:** per-person history and settings, PINs, a sign-in per device, Bonjour and code pairing, Tailscale with automatic fallback


## What's next

🚧 In progress · 🔜 Next, in the order shown · 🗓️ Later · 💡 Idea, not yet scheduled

| Feature | Details | Status |
|---|---|:-:|
| Apple TV on the App Store | With Apple for review | 🚧 In progress |
| Flexarr Server for Mac | The server and web app as a menu bar app for the Mac App Store | 🚧 In progress |
| Android | An app for phones and tablets, with Android TV and Chromecast after it. M–L. [#49](https://github.com/danleach-glitch/flexarr/discussions/49) | 🚧 In progress |
| Subtitles | Teletext, closed captions and height (S); styled ASS after (M). [#40](https://github.com/danleach-glitch/flexarr/discussions/40) | 🔜 1 |
| HDR10+ | Dynamic HDR metadata on the TVs that show it. S–M. [#41](https://github.com/danleach-glitch/flexarr/discussions/41) | 🔜 1 |
| Watchlist | Films and shows to watch later, per profile, whether you have them or not. S–M. [#39](https://github.com/danleach-glitch/flexarr/discussions/39) | 🔜 2 |
| Multiview | Up to four live channels at once. M. [#38](https://github.com/danleach-glitch/flexarr/discussions/38) | 🔜 3 |
| Cast and trivia | Who's on screen and facts about the title while you watch. M. [#43](https://github.com/danleach-glitch/flexarr/discussions/43) | 🔜 4 |
| Family filter | Mute language and skip scenes, with parental ratings for profiles. M. [#44](https://github.com/danleach-glitch/flexarr/discussions/44) | 🔜 4 |
| TrueHD Atmos with height channels | Object audio kept for receivers that decode it. M. [#45](https://github.com/danleach-glitch/flexarr/discussions/45) | 🔜 4 |
| Music visualiser | On Now Playing. S–M. [#46](https://github.com/danleach-glitch/flexarr/discussions/46) | 🔜 5 |
| Mixes and radio by sound | Built from how songs sound, not just their tags. L. [#47](https://github.com/danleach-glitch/flexarr/discussions/47) | 🔜 5 |
| Photo libraries | [#48](https://github.com/danleach-glitch/flexarr/discussions/48) | 🗓️ Later |
| PostgreSQL | As an alternative to SQLite. [#14](https://github.com/danleach-glitch/flexarr/issues/14) | 🗓️ Later |
| New files noticed at once | In libraries the \*arr apps don't manage, rather than at the next scan. [#3](https://github.com/danleach-glitch/flexarr/issues/3) | 🗓️ Later |
| Player controls | Audio sync, a sleep timer for video, speed on the web, stereo or 5.1 output with centre boost. S each. [#125](https://github.com/danleach-glitch/flexarr/discussions/125) | 💡 Idea |
| Find subtitles | Search and download subtitles from the player for files that have none. S–M. [#124](https://github.com/danleach-glitch/flexarr/discussions/124) | 💡 Idea |
| Subtitles made on the device | For titles that have none. [#111](https://github.com/danleach-glitch/flexarr/discussions/111) | 💡 Idea |
| "What did they say?" | Replay the last few seconds with subtitles. [#112](https://github.com/danleach-glitch/flexarr/discussions/112) | 💡 Idea |
| Tracker sync | Trakt, Simkl, MyAnimeList and AniList: scrobbling, history, ratings and scores. M. [#128](https://github.com/danleach-glitch/flexarr/discussions/128) | 💡 Idea |
| iPhone as a remote | Full control of what the Apple TV is playing. M. [#127](https://github.com/danleach-glitch/flexarr/discussions/127) | 💡 Idea |
| Flexarr in your language | Translated apps, a language per profile. M, then ongoing. [#126](https://github.com/danleach-glitch/flexarr/discussions/126) | 💡 Idea |
| Watching together | Shared progress for the people in the room. [#20](https://github.com/danleach-glitch/flexarr/discussions/20) | 💡 Idea |
| "Tonight" picker | One button for what to watch now. [#21](https://github.com/danleach-glitch/flexarr/discussions/21) | 💡 Idea |
| Dialogue boost and night mode | [#23](https://github.com/danleach-glitch/flexarr/discussions/23) | 💡 Idea |
| Search what people say | Find a scene by its dialogue. [#24](https://github.com/danleach-glitch/flexarr/discussions/24) | 💡 Idea |
| Skip more than intros | Recaps, "Previously on" and post-credits flags. [#25](https://github.com/danleach-glitch/flexarr/discussions/25) | 💡 Idea |
| Ready for a trip | Get films ready to take away, and smooth Up Next on weak Wi-Fi. [#26](https://github.com/danleach-glitch/flexarr/discussions/26) | 💡 Idea |
| New-episode notifications | For the shows you follow. [#27](https://github.com/danleach-glitch/flexarr/discussions/27) | 💡 Idea |
| Your year in review | [#29](https://github.com/danleach-glitch/flexarr/discussions/29) | 💡 Idea |
| "Still watching?" | Stop autoplay after a few episodes in a row. [#113](https://github.com/danleach-glitch/flexarr/discussions/113) | 💡 Idea |
| A calm pause screen | With the title's artwork. [#114](https://github.com/danleach-glitch/flexarr/discussions/114) | 💡 Idea |
| Chapters in the player | With a picture for each. [#115](https://github.com/danleach-glitch/flexarr/discussions/115) | 💡 Idea |
| Resume or start over | Ask which, when a title is part-watched. [#116](https://github.com/danleach-glitch/flexarr/discussions/116) | 💡 Idea |
| Shared intro markers | Fill in missing intros from a shared marker database. [#118](https://github.com/danleach-glitch/flexarr/discussions/118) | 💡 Idea |
| Films in several parts | Play as one. [#119](https://github.com/danleach-glitch/flexarr/discussions/119) | 💡 Idea |
| Storage manager | Free up space together with Sonarr and Radarr. [#22](https://github.com/danleach-glitch/flexarr/discussions/22) | 💡 Idea |
| Top Shelf: what's new | When nothing's in progress. [#117](https://github.com/danleach-glitch/flexarr/discussions/117) | 💡 Idea |
| Podcast chapters | [#30](https://github.com/danleach-glitch/flexarr/discussions/30) | 💡 Idea |
| Skip podcast intros and outros | [#31](https://github.com/danleach-glitch/flexarr/discussions/31) | 💡 Idea |
| Podcast transcripts | That follow along. [#32](https://github.com/danleach-glitch/flexarr/discussions/32) | 💡 Idea |
| Voice boost and silence trimming | For podcasts. [#33](https://github.com/danleach-glitch/flexarr/discussions/33) | 💡 Idea |
| AirPlay from the web | From the web app's music bar. [#34](https://github.com/danleach-glitch/flexarr/discussions/34) | 💡 Idea |
| Podcasts on Apple TV | [#35](https://github.com/danleach-glitch/flexarr/discussions/35) | 💡 Idea |
