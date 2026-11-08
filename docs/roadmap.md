# Roadmap

What's built, what's next and in what order. Each planned item links to its discussion under [Ideas](https://github.com/danleach-glitch/flexarr/discussions/categories/ideas), where the detail, scope and votes live. What has shipped is in the [changelog](../CHANGELOG.md).

Sizes are rough: **S** a day or two, **M** about a week, **L** several weeks.

## What's next

🚧 In progress · 🕐 Next, numbered in the order we'll do them · 🗓️ Later · 💡 Idea, not yet scheduled

| Feature | Details | Status |
|---|---|:-:|
| Apple TV on the App Store | With Apple for review. Until then, [install it from Xcode](https://flexarr.io/guide/apple-tv-xcode) | 🚧 |
| The Mac app on the App Store | It [downloads from flexarr.io](https://flexarr.io/apps) now; the App Store copy follows the Apple TV app | 🚧 |
| Flexarr Server for Mac | The server and web app as a menu bar app for the Mac App Store | 🚧 |
| Android on Google Play | Phones, tablets, Android TV and Google TV, with Chromecast and Android Auto. In a closed test on Google Play. [#49](https://github.com/danleach-glitch/flexarr/discussions/49) | 🚧 |
| New files noticed at once | In libraries the \*arr apps don't manage, rather than at the next scan. M. [#3](https://github.com/danleach-glitch/flexarr/issues/3) | 🕐 1 |
| Flexarr in your language | Translated apps, a language per profile. Every app and the web are ready for translation; the languages come next. M, then ongoing. [#126](https://github.com/danleach-glitch/flexarr/discussions/126) | 🕐 2 |
| TrueHD Atmos with height channels | Object audio kept for receivers that decode it. Android TV already sends TrueHD as it is. M. [#45](https://github.com/danleach-glitch/flexarr/discussions/45) | 🕐 3 |
| Multiview | Up to four live channels at once. M. [#38](https://github.com/danleach-glitch/flexarr/discussions/38) | 🕐 4 |
| Mixes and radio by sound | Built from how songs sound, not just their tags. L. [#47](https://github.com/danleach-glitch/flexarr/discussions/47) | 🕐 5 |
| More tracker sync | Trakt is built. Still to come: ratings, then Simkl, MyAnimeList and AniList. M. [#128](https://github.com/danleach-glitch/flexarr/discussions/128) | 🗓️ |
| A calm pause screen | With the title's artwork. [#114](https://github.com/danleach-glitch/flexarr/discussions/114) | 💡 |
| Storage manager | Free up space together with Sonarr and Radarr. [#22](https://github.com/danleach-glitch/flexarr/discussions/22) | 💡 |

## Done

| Feature | Details | Status |
|---|---|:-:|
| Foundations | Server, API-key auth, CI (server, web, Apple, Docker, CodeQL), Docker image with PUID/PGID and VA-API/Quick Sync, macOS deploy, semver releases, Settings › Updates with one-click updates on a Mac, backups, logs and opt-in error reports | ✅ |
| Library | Scanner for films, TV, music and audiobooks, file name parsing, ffprobe media info, webhook-driven and scheduled rescans, Fix match and editing details by hand, delete through Sonarr and Radarr, library health, Plex history import, media folders that find themselves, libraries without the \*arr apps, Home videos | ✅ |
| Metadata | From Sonarr, Radarr and Lidarr with no keys of our own, local `.nfo` files and artwork, TVmaze, TMDB with your own key, IMDb and Rotten Tomatoes scores, image cache, music artwork and album kinds | ✅ |
| Playback | Direct play on the device (AV1, VC-1 and the rest), repackaging and hardware transcoding, HDR10, HDR10+, HLG and Dolby Vision 5, 7 and 8.1, Atmos passthrough and lossless surround, subtitles with timing, size, position and the system caption style, styled ASS, closed captions and teletext, subtitle search with Bazarr, dialogue boost, night mode and stereo or 5.1 speakers, audio sync, a sleep timer, speed kept per show, skip intro, recap and credits, Up Next, scrubbing previews, near-instant starts | ✅ |
| Finding things | Home rows you arrange, libraries of the same kind combined on Home, search, person and genre pages, collections (manual, smart and Radarr series), a watchlist that follows titles until they arrive, Coming soon, Discover and Seerr requests for films, shows, seasons and music, shuffle | ✅ |
| Music | Queue, playlists, synced lyrics, daily mixes, artist radio, near-gapless, crossfade, volume normalisation, offline albums, a visualiser on Now Playing | ✅ |
| Podcasts | Apple's directory and any feed, chapters, following per profile, Up Next across devices, speeds and sleep timer, OPML, offline and automatic downloads, notifications, widgets, CarPlay (awaiting Apple) | ✅ |
| Audiobooks | A library of books from your folders: chapters, series, authors and narrators, speed per book, smart rewind, sleep timer, bookmarks, author photos and biographies, offline listening on iPhone and Android, CarPlay and Android Auto, and the Apple TV. An import from Audiobookshelf next. [Plan](audiobooks.md) | ✅ |
| Live TV | Dispatcharr sources, guide, favourites, pause and rewind, recording and series recording | ✅ |
| Apple apps | Apple TV, iPhone, iPad and Mac with Flexarr's own player, profiles, Top Shelf with title logos, downloads and offline mode, Picture in Picture and AirPlay; the iPhone and iPad app on the App Store, the Mac app as a download | ✅ |
| Android apps | Phones, tablets, Android TV and Google TV: downloads, podcasts and audiobooks, Chromecast, Android Auto, Google TV's Continue watching, frame-rate matching and lossless surround | ✅ |
| Control | Siri and Shortcuts (play, shuffle, on another device), play on another device, the iPhone as an Apple TV remote with Lock Screen controls, an MCP server for assistants, [Home Assistant](https://github.com/danleach-glitch/flexarr-home-assistant) (media players and playback triggers) | ✅ |
| Profiles and access | Per-person history and settings, PINs and an admin PIN, kids profiles with age ratings and child lock, a sign-in per device, Bonjour and code pairing, Tailscale with automatic fallback, family invites to watch from afar with Tailscale built into the apps, a name for your server | ✅ |
| Tracking | [Trakt](https://trakt.tv): history both ways, scrobbling, watchlist sync, Trakt lists in Collections and picks on Discover | ✅ |
| Languages | Every word on screen comes from a language pack, ready for translations | ✅ |
