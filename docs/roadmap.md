# Roadmap

What's built, what's next and in what order. Each planned item links to its discussion under [Ideas](https://github.com/danleach-glitch/flexarr/discussions/categories/ideas), where the detail, scope and votes live. What has shipped is in the [changelog](../CHANGELOG.md).

Sizes are rough: **S** a day or two, **M** about a week, **L** several weeks.

## What's next

🚧 In progress · 🕐 Next, numbered in the order we'll do them · 🗓️ Later · 💡 Idea, not yet scheduled

| Feature | Details | Status |
|---|---|:-:|
| Apple TV on the App Store | With Apple for review | 🚧 |
| Flexarr Server for Mac | The server and web app as a menu bar app for the Mac App Store | 🚧 |
| Android | An app for phones and tablets, with Chromecast, and Android TV and Google TV after it. M–L. [#49](https://github.com/danleach-glitch/flexarr/discussions/49) | 🚧 |
| Tracker sync | Trakt is built (on its way in the next release). Still to come: ratings, then Simkl, MyAnimeList and AniList. M. [#128](https://github.com/danleach-glitch/flexarr/discussions/128) | 🚧 |
| Invite family to watch from afar | A link or code connects their apps, with Tailscale built in: no account, sign-in or setup for them. M–L. [#146](https://github.com/danleach-glitch/flexarr/discussions/146) | 🚧 |
| Flexarr in your language | Translated apps, a language per profile. M, then ongoing. [#126](https://github.com/danleach-glitch/flexarr/discussions/126) | 🕐 1 |
| TrueHD Atmos with height channels | Object audio kept for receivers that decode it. M. [#45](https://github.com/danleach-glitch/flexarr/discussions/45) | 🕐 2 |
| Multiview | Up to four live channels at once. M. [#38](https://github.com/danleach-glitch/flexarr/discussions/38) | 🕐 3 |
| Mixes and radio by sound | Built from how songs sound, not just their tags. L. [#47](https://github.com/danleach-glitch/flexarr/discussions/47) | 🕐 4 |
| Audiobooks | A library for audiobooks with Readarr, Bookshelf or Chaptarr: chapters, series, narrators, bookmarks, offline listening, requests and an import from Audiobookshelf. L. [Plan](audiobooks.md), [#144](https://github.com/danleach-glitch/flexarr/discussions/144) | 🗓️ |
| New files noticed at once | In libraries the \*arr apps don't manage, rather than at the next scan. [#3](https://github.com/danleach-glitch/flexarr/issues/3) | 🗓️ |
| Skip more than intros | Recaps, "Previously on" and post-credits flags. [#25](https://github.com/danleach-glitch/flexarr/discussions/25) | 💡 |
| A calm pause screen | With the title's artwork. [#114](https://github.com/danleach-glitch/flexarr/discussions/114) | 💡 |
| Storage manager | Free up space together with Sonarr and Radarr. [#22](https://github.com/danleach-glitch/flexarr/discussions/22) | 💡 |
| Podcast chapters | [#30](https://github.com/danleach-glitch/flexarr/discussions/30) | 💡 |

## Done

| Feature | Details | Status |
|---|---|:-:|
| Foundations | Server, API-key auth, CI (server, web, Apple, Docker, CodeQL), Docker image with PUID/PGID and VA-API/Quick Sync, macOS deploy, semver releases, Settings › Updates with one-click updates on a Mac, backups, logs and opt-in error reports | ✅ |
| Library | Scanner for films, TV and music, file name parsing, ffprobe media info, webhook-driven and scheduled rescans, Fix match, delete through Sonarr and Radarr, library health, Plex history import, media folders that find themselves, libraries without the \*arr apps, Home videos | ✅ |
| Metadata | From Sonarr, Radarr and Lidarr with no keys of our own, local `.nfo` files and artwork, TVmaze, TMDB with your own key, image cache, music artwork and album kinds | ✅ |
| Playback | Direct play on the device (AV1, VC-1 and the rest), repackaging and hardware transcoding, HDR10, HDR10+, HLG and Dolby Vision 5, 7 and 8.1, Atmos passthrough and lossless surround, subtitles with timing, size, position and the system caption style, styled ASS, closed captions and teletext, subtitle search with Bazarr, dialogue boost, night mode and stereo or 5.1 speakers, audio sync, a sleep timer, speed kept per show, skip intro, recap and credits, Up Next, scrubbing previews | ✅ |
| Finding things | Home rows you arrange, libraries of the same kind combined on Home, search, person and genre pages, collections (manual, smart and Radarr series), a watchlist that follows titles until they arrive, Coming soon, Discover and Seerr requests, shuffle | ✅ |
| Music | Queue, playlists, synced lyrics, daily mixes, artist radio, near-gapless, crossfade, volume normalisation, offline albums, a visualiser on Now Playing | ✅ |
| Podcasts | Apple's directory and any feed, following per profile, Up Next across devices, speeds and sleep timer, OPML, offline and automatic downloads, notifications, widgets, CarPlay (awaiting Apple) | ✅ |
| Live TV | Dispatcharr sources, guide, favourites, pause and rewind, recording and series recording | ✅ |
| Apple apps | Apple TV, iPhone and iPad with Flexarr's own player, profiles, Top Shelf with title logos, downloads, Picture in Picture and AirPlay; the iPhone and iPad app on the App Store | ✅ |
| Control | Siri and Shortcuts (play, shuffle, on another device), play on another device, the iPhone as an Apple TV remote with Lock Screen controls, an MCP server for assistants, [Home Assistant](https://github.com/danleach-glitch/flexarr-home-assistant) (media players and playback triggers) | ✅ |
| Profiles and access | Per-person history and settings, PINs and an admin PIN, kids profiles with age ratings and child lock, a sign-in per device, Bonjour and code pairing, Tailscale with automatic fallback | ✅ |
