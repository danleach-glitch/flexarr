# Flexarr

**A self-hosted, open-source media server for your films, TV shows and music — built to sit alongside the \*arr stack.**

Flexarr aims to be a full Plex-style media server: it organises your library, fetches metadata and artwork, and streams to your devices with direct play or on-the-fly transcoding. Unlike Plex it is fully open source, needs no cloud account, and treats [Sonarr](https://sonarr.tv), [Radarr](https://radarr.video), [Lidarr](https://lidarr.audio) and [Prowlarr](https://prowlarr.com) as first-class neighbours rather than afterthoughts.

> **Status:** early development. The foundations (server, web client, Apple TV client, CI) are in place; library scanning and playback are next. See the [roadmap](docs/roadmap.md).

## Features (planned)

- 🎬 Movies, 📺 TV shows and 🎵 music libraries with rich metadata (TMDB, TVDB, MusicBrainz)
- ▶️ Direct play, direct stream and FFmpeg transcoding with hardware acceleration
- 🔗 \*arr integration: instant library updates from Sonarr/Radarr/Lidarr webhooks, request missing content, see download queues
- 👥 Multiple users, profiles, watch history and resume
- 📱 Clients: web and Apple TV first, then iOS/iPadOS, Android/Android TV and more

## Repository layout

| Path | What |
| --- | --- |
| `src/Flexarr.Api` | ASP.NET Core server: REST API (`/api/v1`), webhooks, serves the web client |
| `src/Flexarr.Core` | Domain model, filename parsing, library scanning |
| `src/Flexarr.Integrations.Arr` | Sonarr / Radarr / Lidarr clients and webhook handling |
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

Mount your media at the **same path** Sonarr/Radarr/Lidarr use, so the paths in their webhooks match what Flexarr sees.

## Connect Sonarr / Radarr / Lidarr

In each app go to **Settings → Connect → + → Webhook** and set:

- **URL:** `http://<flexarr-host>:8383/api/v1/webhooks/sonarr?apikey=<your-api-key>` (use `radarr` / `lidarr` as appropriate)
- **Method:** POST
- **Triggers:** On Import, On Upgrade, On Rename, On Delete

Press **Test** — Flexarr answers with `200 OK`. See [docs/arr-integration.md](docs/arr-integration.md) for details.

## Development

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 24+, and for the Apple clients Xcode 26+ with [XcodeGen](https://github.com/yonaskolb/XcodeGen).

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
