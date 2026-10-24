# Flexarr

**A self-hosted, open-source media server for your films, TV shows and music — built to sit alongside the \*arr stack.**

Flexarr aims to be a full Plex-style media server: it organises your library, fetches metadata and artwork, and streams to your devices with direct play or on-the-fly transcoding. Unlike Plex it is fully open source, needs no cloud account, and treats [Sonarr](https://sonarr.tv), [Radarr](https://radarr.video), [Lidarr](https://lidarr.audio) and [Prowlarr](https://prowlarr.com) as first-class neighbours rather than afterthoughts.

> **Status:** version 0.8, early but running a real 12,000-file library day to day. Web is the main client; the Apple TV, iPhone and iPad apps are early (see [Apple TV, iPhone and iPad](#apple-tv-iphone-and-ipad)). See the [changelog](CHANGELOG.md) and the [roadmap](docs/roadmap.md).

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
- **Collections**: your own, smart ones that fill themselves from rules (e.g. *Top 10 shows this year*), and film series from Radarr, any of which can be a row on Home
- **Coming soon**: what Sonarr and Radarr expect this month, with download progress
- **Requests**: with Seerr connected, see what's trending that you don't have and request it in one click

**For the whole household**
- Profiles with their own Continue Watching, history and Up Next, an optional PIN, and a profile picker when Flexarr opens, on the web and on Apple TV
- Choose which libraries each profile can see
- Each browser and Apple TV gets a sign-in of its own, which you can sign out from Settings; an Apple TV connects by scanning a QR code
- **Away from home over Tailscale**, with no ports opened on your router

**Things Plex doesn't do**
- **Bring your history over from Plex**: watched state and resume points, matched by file, without touching Plex
- **Library health**: a health score, duplicate copies (and the space they use), unreadable files, standard-definition titles with a "find a better copy" button that asks Radarr/Sonarr, and titles whose match is worth checking
- **Fix match**: correct a wrongly matched film or show by picking the right one from Sonarr or Radarr, and it stays fixed
- **Delete** a film, show or episode from its page, through Sonarr and Radarr, or by Flexarr itself for titles they don't manage
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
| `clients/apple/` | Apple clients: `FlexarrKit` (shared Swift package), `Shared/` app code, and the tvOS and iOS apps |
| `docs/` | Architecture, \*arr integration, roadmap |

## Quick start (Docker)

```bash
docker run -d --name flexarr \
  -p 8383:8383 \
  -v /path/to/config:/config \
  -v /path/to/media:/data/media \
  ghcr.io/danleach-glitch/flexarr:latest
```

`latest` is the newest release (each is listed under [Releases](https://github.com/danleach-glitch/flexarr/releases)); pin a version such as `0.8` or `0.8.0` to upgrade when you choose, or use `main` for every change as it lands.

Open <http://localhost:8383>. On first run Flexarr generates an API key and saves it to `/config/config.json`. Sign in to the web app with it once; each browser and Apple TV then gets a sign-in of its own, listed in Settings › Server where you can sign any of them out.

Plain `http://` is fine on a home network you trust. From anywhere else, use Tailscale (Settings › Remote access) or an HTTPS reverse proxy, so the key and sign-ins aren't sent in the clear. A reverse proxy on another machine or in Docker must be listed in `trustedProxies`, or Flexarr treats everything it forwards as the internet.

Mount your media at the **same path** Sonarr/Radarr/Lidarr use, or add `pathMappings` to `config.json`, so the paths in their webhooks match what Flexarr sees.

### macOS (native)

On a Mac, run Flexarr natively so it can use hardware transcoding: `make deploy DEPLOY_HOST=<ssh-host>`. See [docs/install-macos.md](docs/install-macos.md).

## Connect Sonarr / Radarr / Lidarr

In each app go to **Settings → Connect → + → Webhook** and set:

- **URL:** `http://<flexarr-host>:8383/api/v1/webhooks/sonarr?secret=<webhook-secret>` (use `radarr` / `lidarr` as appropriate). Settings › Server shows the addresses with the secret filled in.
- **Method:** POST
- **Triggers:** On Import, On Upgrade, On Rename, On Delete

Press **Test** — Flexarr answers with `200 OK`. See [docs/arr-integration.md](docs/arr-integration.md) for details.

## Apple TV, iPhone and iPad

The apps (in `clients/apple`) install from Xcode onto your own devices. For an Apple TV:

1. Sign Xcode in to your Apple ID (Xcode › Settings › Accounts). A free one works.
2. Pair the Apple TV: on the TV, open Settings › Remotes and Devices › Remote App and Devices; on the Mac, open Devices and Simulators (⇧⌘2) and pair it.
3. Run `make apple-install TEAM=<your team id> BUNDLE_ID=com.yourname.flexarr.tv`. Your team id is in Xcode › Settings › Accounts; the identifier just needs to be unique to you.

For an iPhone or iPad, connect it to the Mac (or pair it over Wi-Fi in Devices and Simulators), turn on Developer Mode on the device (Settings › Privacy & Security), then run `make ios-install TEAM=<your team id> BUNDLE_ID=com.yourname.flexarr.ios`.

With a free Apple ID the apps stop opening after 7 days; run the same command again. In the app, pick your server and approve the code it shows in the web app (Settings › Connect a TV; on an iPhone, **Approve in Safari** opens it for you), or type the API key; either way the device gets a sign-in of its own rather than keeping the key. On iPhone and iPad, films play in Picture in Picture, over AirPlay and with the screen locked.

## Development

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 24+, FFmpeg 7+ (`ffmpeg` and `ffprobe` on the PATH), and for the Apple clients Xcode 26+ with [XcodeGen](https://github.com/yonaskolb/XcodeGen).

```bash
make setup     # restore .NET + npm dependencies
make dev-api   # server on http://localhost:8383 (hot reload)
make dev-web   # web client on http://localhost:5173 (proxies /api to the server)
make test      # server + web tests
make apple     # generate the Xcode project and build the tvOS and iOS apps
```

`docker compose up -d` starts Flexarr together with Sonarr, Radarr, Lidarr and Prowlarr for integration testing.

See [CONTRIBUTING.md](CONTRIBUTING.md) for conventions and [docs/architecture.md](docs/architecture.md) for how the pieces fit together.

## License

Flexarr is licensed under the [GNU GPL v3.0 or later](LICENSE), like Sonarr, Radarr and Lidarr.
