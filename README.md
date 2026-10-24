# Flexarr

**A self-hosted, open-source media server for your films, TV shows and music — built to sit alongside the \*arr stack.**

Flexarr aims to be a full Plex-style media server: it organises your library, fetches metadata and artwork, and streams to your devices with direct play or on-the-fly transcoding. Unlike Plex it is fully open source, needs no cloud account, and treats [Sonarr](https://sonarr.tv), [Radarr](https://radarr.video), [Lidarr](https://lidarr.audio) and [Prowlarr](https://prowlarr.com) as first-class neighbours rather than afterthoughts.

> **Status:** early, but it runs a real 12,000-file library day to day. Web is the main client; the Apple TV app is in progress. See the [roadmap](docs/roadmap.md).

## What it does

**Your library, beautifully**
- Films, TV and music libraries, scanned in minutes and kept current by Sonarr/Radarr webhooks, a 12-hour rescan and a rescan at startup
- Artwork, overviews, genres, ratings, trailers and cast taken from **your Sonarr and Radarr** (no TMDB or TVDB keys), with TVmaze for TV cast; music from tags and cover art in the files
- A design that takes its colour from whatever you're looking at ([design notes](docs/design.md)), readable from the sofa, keyboard-first, and fine on a phone

**Watching**
- Direct play when the device can, otherwise conversion as you watch, with **hardware acceleration** (VideoToolbox on macOS; VA-API, Quick Sync and NVENC elsewhere), HDR-to-SDR conversion and seeking anywhere instantly
- **Skip intro, recap and credits**, found by audio fingerprinting across each season (or from chapter names), plus **Up Next** with a countdown
- Subtitles (embedded and sidecar `.srt`/`.ass`/`.vtt`; picture subtitles burned in), audio track and quality switching, scrubbing previews, resume everywhere, picture-in-picture and AirPlay
- Music with a now-playing bar that keeps going while you browse, and media-key support

**Finding something**
- Home: Continue Watching with Up Next, Top 10 films and shows, genre rows, "Because you watched", new arrivals per library
- ⌘K search across titles, episodes, people and genres; person pages; Discover by genre
- **Coming soon**: what Sonarr and Radarr expect this month, with download progress
- **Requests**: with Seerr connected, see what's trending that you don't have and request it in one click

**For the whole household**
- Profiles with their own Continue Watching, history and Up Next, an optional PIN, and a profile picker when Flexarr opens

**Things Plex doesn't do**
- **Bring your history over from Plex**: watched state and resume points, matched by file, without touching Plex
- **Library health**: duplicate copies (and the space they use), unreadable files, standard-definition titles with a "find a better copy" button that asks Radarr/Sonarr
- No account, no cloud, open source

## Repository layout

| Path | What |
| --- | --- |
| `src/Flexarr.Api` | ASP.NET Core server: REST API (`/api/v1`), playback sessions, background workers, serves the web client |
| `src/Flexarr.Core` | Domain model and database, filename and tag parsing, library scanning, ffprobe |
| `src/Flexarr.Metadata` | Metadata from Sonarr/Radarr/TVmaze, image cache, ambient colours |
| `src/Flexarr.Streaming` | Playback decisions, FFmpeg/HLS transcoding, subtitles, scrubbing previews, intro/credits detection |
| `src/Flexarr.Integrations.Arr` | Sonarr / Radarr / Lidarr / Seerr clients and webhook handling |
| `frontend/` | Web client (React + TypeScript + Vite) |
| `clients/apple/` | Apple clients: `FlexarrKit` (shared Swift package) and the tvOS app |
| `docs/` | Architecture, \*arr integration, roadmap |

## Quick start (Docker)

```bash
docker run -d --name flexarr \
  -p 8383:8383 \
  -v /path/to/config:/config \
  -v /path/to/media:/data/media \
  ghcr.io/danleach-glitch/flexarr:main
```

Open <http://localhost:8383>. On first run Flexarr generates an API key and saves it to `/config/config.json`.

Mount your media at the **same path** Sonarr/Radarr/Lidarr use, or add `pathMappings` to `config.json`, so the paths in their webhooks match what Flexarr sees.

### macOS (native)

On a Mac, run Flexarr natively so it can use hardware transcoding: `make deploy DEPLOY_HOST=<ssh-host>`. See [docs/install-macos.md](docs/install-macos.md).

## Connect Sonarr / Radarr / Lidarr

In each app go to **Settings → Connect → + → Webhook** and set:

- **URL:** `http://<flexarr-host>:8383/api/v1/webhooks/sonarr?apikey=<your-api-key>` (use `radarr` / `lidarr` as appropriate)
- **Method:** POST
- **Triggers:** On Import, On Upgrade, On Rename, On Delete

Press **Test** — Flexarr answers with `200 OK`. See [docs/arr-integration.md](docs/arr-integration.md) for details.

## Development

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 24+, FFmpeg 7+ (`ffmpeg` and `ffprobe` on the PATH), and for the Apple clients Xcode 26+ with [XcodeGen](https://github.com/yonaskolb/XcodeGen).

```bash
make setup     # restore .NET + npm dependencies
make dev-api   # server on http://localhost:8383 (hot reload)
make dev-web   # web client on http://localhost:5173 (proxies /api to the server)
make test      # server + web tests
make apple     # generate the Xcode project and build the tvOS app
```

`docker compose up -d` starts Flexarr together with Sonarr, Radarr, Lidarr and Prowlarr for integration testing.

See [CONTRIBUTING.md](CONTRIBUTING.md) for conventions and [docs/architecture.md](docs/architecture.md) for how the pieces fit together.

## License

Flexarr is licensed under the [GNU GPL v3.0 or later](LICENSE), like Sonarr, Radarr and Lidarr.
