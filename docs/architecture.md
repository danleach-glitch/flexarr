# Architecture

```
                    ┌──────────────── Clients ────────────────┐
                    │  Web (React)   Apple TV / iOS (SwiftUI) │
                    └───────────────────┬─────────────────────┘
                                        │ REST /api/v1  +  HLS / direct streams
┌──────────────┐  webhooks   ┌──────────▼───────────┐   ffprobe / ffmpeg   ┌───────────┐
│ Sonarr       │────────────▶│                      │─────────────────────▶│  FFmpeg   │
│ Radarr       │◀────────────│    Flexarr server    │                      └───────────┘
│ Lidarr       │  REST API   │   (ASP.NET Core)     │   metadata agents    ┌───────────┐
│ Prowlarr     │             │                      │─────────────────────▶│ TMDB/TVDB │
└──────────────┘             └──────────┬───────────┘                      │ MusicBrainz│
                                        │                                  └───────────┘
                             ┌──────────▼───────────┐
                             │ SQLite (default) /   │   + /config: artwork cache,
                             │ PostgreSQL           │     transcodes, logs
                             └──────────────────────┘
```

## Why this stack

- **C# / .NET for the server.** Sonarr, Radarr, Lidarr, Prowlarr (and Jellyfin) are all .NET. Sharing the language and conventions makes it easy for the \*arr community to contribute, and .NET gives us high-throughput async I/O for streaming, solid cross-platform support (Linux/Windows/macOS, x64/ARM) and single-binary publishing.
- **React + TypeScript for the web client**, the same choice the \*arr apps made.
- **Native SwiftUI for Apple platforms.** tvOS needs a native app to get AVPlayer, the focus engine, Top Shelf and proper HDR/Dolby support. Networking and models live in the `FlexarrKit` Swift package, shared by tvOS, iOS and macOS targets.
- **FFmpeg** for probing and transcoding, with hardware acceleration (VAAPI, QSV, NVENC, VideoToolbox).

## Server projects

| Project | Responsibility |
| --- | --- |
| `Flexarr.Api` | Host: HTTP API, auth, playback sessions, background workers (scanning, intro detection, scrubbing previews), static web client |
| `Flexarr.Core` | Domain and EF Core database (SQLite), parsing, scanning, ffprobe — no ASP.NET dependency |
| `Flexarr.Metadata` | Metadata refresh from Sonarr/Radarr/TVmaze, image cache (resizing with ffmpeg), ambient colour extraction |
| `Flexarr.Streaming` | Playback decisions, FFmpeg command building, HLS transcode sessions, subtitles, scrubbing previews, audio fingerprinting for intros/credits |
| `Flexarr.Integrations.Arr` | Sonarr/Radarr/Lidarr and Seerr API clients, webhook translation |

## Key flows

**Library update from an \*arr.** Sonarr imports an episode → POSTs its webhook to `/api/v1/webhooks/sonarr` → the path is mapped to Flexarr's view of the disk → the series folder is rescanned (bursts are coalesced) → new files are probed with ffprobe → metadata for new items is refreshed from Sonarr.

**Metadata.** For each library, Flexarr fetches everything Radarr/Sonarr know (one call per app, plus episodes and credits) and matches items by folder path, then IMDb/TMDB/TVDB id. Titles the *arr apps don't manage are found through their lookup endpoints (which proxy TMDB/TVDB without keys). Artwork URLs are stored; images are fetched, resized and cached on first view, and an ambient colour is sampled from each.

**Playback.** The client reports what it can play (from `canPlayType` in browsers). If the file fits, it's served as-is with range requests. Otherwise Flexarr creates a transcode session: a full VOD playlist of 6-second segments is returned immediately; each segment request starts (or reuses) an FFmpeg process positioned at that segment, with keyframes forced on segment boundaries and timestamps offset to match, so seeking anywhere is instant and segments from different runs line up. FFmpeg is paused (SIGSTOP) when it gets far ahead of the viewer. On macOS decode, scaling, HDR→SDR conversion and encode all run on the media engine.

**Intros and credits.** For each season, the first 8 and last 5 minutes of audio of every episode are fingerprinted (32-bit sub-fingerprints every 8 ms from band-energy differences) and compared with neighbouring episodes: offset voting on exact matches finds the alignment, then the longest run with low bit error is the shared theme or credits. Chapter titles ("Intro", "Credits") take precedence. A background worker works through the library, prioritising what's being watched and giving way to viewers.

## Authentication

\*arr-style API keys: send `X-Api-Key: <key>` or `?apikey=<key>`. The key is generated on first run and stored in `<DataDirectory>/config.json`. `AuthenticationRequired` can be `Enabled` or `DisabledForLocalAddresses`. User accounts with per-user tokens come with multi-user support.
