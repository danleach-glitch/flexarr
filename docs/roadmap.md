# Roadmap

Web and Apple TV are the first-class clients. Done items stay listed so the shape of the project is visible.

## Done

- **Foundations:** server, API-key auth, CI (server, web, Apple, Docker, CodeQL), Docker image, macOS deploy
- **Library:** SQLite database, scanner (films, TV, music), parsers (`Title (Year)`, `SxxEyy`, multi-episode, daily, specials, music tags), ffprobe media info, webhook-driven and scheduled rescans
- **Metadata:** Sonarr/Radarr as the source (no keys), lookups for unmanaged titles, TVmaze cast, image cache with ambient colours, local and embedded music artwork
- **Playback:** direct play / transcode decisions, HLS with seek-anywhere and hardware acceleration (VideoToolbox, VA-API, QSV, NVENC), HDR→SDR, subtitles (text and burned-in picture), audio/quality switching, resume and watch state, Up Next, intro/recap/credits detection, scrubbing previews
- **Web client:** home rows, library grids, detail pages, person and genre pages, ⌘K search, player, music player, settings
- **Beyond Plex:** Plex history import (per profile), Coming soon and downloads, library health, Seerr requests, Emby/Jellyfin-compatible endpoints for the *arr connection
- **Profiles:** per-person watch history, Continue Watching and Up Next, optional PIN, profile picker (web and Apple TV), per-profile library access
- **Collections:** manual, smart (rule-based) and Radarr film series, with suggestions, on the web
- **Sign-in and remote access:** a sign-in per browser and TV, Bonjour server discovery and QR-code pairing for the Apple TV, Tailscale
- **Library care:** Fix match, delete through Sonarr/Radarr or directly, held folders when a drive looks unmounted

## Next

- **Apple TV app:** browsing, detail pages, profiles and AVPlayer playback with skip intro and Up Next are in; Collections next
- **iPhone and iPad app:** the same as the Apple TV app, with Picture in Picture and AirPlay; Collections and downloads for offline next
- **Profiles, part two:** parental ratings
- **PostgreSQL** as an alternative to SQLite (#14)
- **File watching** for libraries not managed by the *arr apps (#3)

## Later

Playlists, offline downloads, Chromecast, Live TV and DVR, Android and Android TV, statistics, music metadata from Lidarr/MusicBrainz.
