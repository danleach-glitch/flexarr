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
| `Flexarr.Api` | Host: HTTP API, auth, background workers, static web client |
| `Flexarr.Core` | Domain (libraries, media items), parsing, scanning abstractions — no ASP.NET dependency |
| `Flexarr.Integrations.Arr` | \*arr API clients and webhook translation |

Planned additions: `Flexarr.Data` (EF Core; SQLite + PostgreSQL providers), `Flexarr.Metadata` (TMDB/TVDB/MusicBrainz agents), `Flexarr.Streaming` (playback decisions, FFmpeg/HLS).

## Key flows

**Library update from an \*arr.** Sonarr imports an episode → POSTs its webhook to `/api/v1/webhooks/sonarr` → `ArrWebhookTranslator` turns it into a `LibraryScanRequest` for the series folder → the request goes on `ILibraryScanQueue` → `LibraryScanWorker` rescans just that folder. No periodic full-library scans needed.

**Playback (planned).** A client sends its device profile (containers, codecs, max bitrate). The server picks direct play, direct stream (remux) or transcode, and serves either the file with range requests or an HLS playlist backed by an FFmpeg session.

## Authentication

\*arr-style API keys: send `X-Api-Key: <key>` or `?apikey=<key>`. The key is generated on first run and stored in `<DataDirectory>/config.json`. `AuthenticationRequired` can be `Enabled` or `DisabledForLocalAddresses`. User accounts with per-user tokens come with multi-user support.
