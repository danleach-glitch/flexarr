# Roadmap

Web and Apple TV are the first-class clients. Done items stay listed so the shape of the project is visible.

## Done

- **Foundations:** server, API-key auth, CI (server, web, Apple, Docker, CodeQL), Docker image, macOS deploy
- **Library:** SQLite database, scanner (films, TV, music), parsers (`Title (Year)`, `SxxEyy`, multi-episode, daily, specials, music tags), ffprobe media info, webhook-driven and scheduled rescans
- **Metadata:** Sonarr/Radarr as the source (no keys), lookups for unmanaged titles, TVmaze cast, image cache with ambient colours, local and embedded music artwork
- **Playback:** direct play / transcode decisions, HLS with seek-anywhere and hardware acceleration (VideoToolbox, VA-API, QSV, NVENC), HDR→SDR, subtitles (text and burned-in picture), audio/quality switching, resume and watch state, Up Next, intro/recap/credits detection, scrubbing previews
- **Web client:** home rows, library grids, detail pages, person and genre pages, ⌘K search, player, music player, settings
- **Beyond Plex:** Plex history import, Coming soon and downloads, library health, Seerr requests

## Next

- **Apple TV app:** browsing, detail pages and AVPlayer playback with skip intro and Up Next (in progress)
- **Accounts:** multiple users with their own watch history and profiles, PIN, parental ratings; per-user Plex import (#15)
- **PostgreSQL** as an alternative to SQLite (#14)
- **File watching** for libraries not managed by the *arr apps (#3)
- **Server discovery** over Bonjour for the Apple TV app (#11)
- **Emby/Jellyfin-compatible refresh endpoint** so *arr apps work with their built-in connection (#10)
- **iOS/iPadOS** from the shared `FlexarrKit`

## Later

Collections and playlists, offline downloads, Chromecast, Live TV and DVR, Android and Android TV, statistics, music metadata from Lidarr/MusicBrainz.
