# Roadmap

Milestones are tracked on [GitHub](https://github.com/danleach-glitch/flexarr/milestones). Order is roughly priority; web and Apple TV are the first-class clients.

## M0 — Foundations ✅
Server skeleton, API-key auth, \*arr webhook intake, web client shell, tvOS app shell, CI (server/web/Apple), Docker image on GHCR, CodeQL, Dependabot.

## M1 — Library
- Database (EF Core; SQLite default, PostgreSQL optional) and migrations
- Library management API + UI (movies, TV, music; multiple root folders)
- Scanner: filesystem walk, file watching, `ffprobe` media info
- Parsers: movies (`Title (Year)`), TV (`SxxEyy`, daily, anime absolute), music (tags)
- Metadata agents: TMDB, TVDB, MusicBrainz, fanart.tv; local NFO/artwork from the \*arrs
- Artwork cache and image resizing

## M2 — Playback
- Device profiles and direct play / direct stream / transcode decisions
- HLS transcoding with FFmpeg, hardware acceleration
- Subtitles (embedded, sidecar, burn-in), audio track selection
- Music streaming, gapless playback
- Watch state, resume points, "continue watching"/"on deck"

## M3 — Clients
- Web: browse, detail pages, search, player
- Apple TV: server discovery, home rows, detail pages, AVPlayer playback, Top Shelf
- iOS/iPadOS from the same `FlexarrKit`

## M4 — Users & access
- Accounts, managed profiles, PIN, parental ratings
- Per-user libraries and history; OIDC login
- Remote access guide (reverse proxy, TLS)

## M5 — Deeper \*arr integration
Emby/Jellyfin-compatible refresh endpoint, calendars, queue, requests, ID-based matching (see [arr-integration.md](arr-integration.md)).

## Later
Collections & playlists, intro/credits detection, trickplay thumbnails, offline downloads, AirPlay/Chromecast, Live TV & DVR, Android/Android TV, statistics.
