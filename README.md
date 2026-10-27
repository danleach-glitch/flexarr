<p align="center">
  <img src="site/logo-mark.svg" width="72" alt="">
</p>

<h1 align="center">Flexarr</h1>

<p align="center"><strong>Your entertainment. Every screen.</strong></p>

<p align="center">
  Your own streaming service for the films, TV, music and podcasts you already have.<br>
  Apple TV, iPhone, iPad and the web. Built for <a href="https://sonarr.tv">Sonarr</a>, <a href="https://radarr.video">Radarr</a> and <a href="https://lidarr.audio">Lidarr</a>. No ads, no account, no subscription.
</p>

<p align="center">
  <a href="https://github.com/danleach-glitch/flexarr/releases"><img src="https://img.shields.io/github/v/release/danleach-glitch/flexarr?display_name=tag&color=a9c2ec&labelColor=1b1720" alt="Latest release"></a>
  <a href="https://github.com/danleach-glitch/flexarr/actions/workflows/ci.yml"><img src="https://github.com/danleach-glitch/flexarr/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/danleach-glitch/flexarr/pkgs/container/flexarr"><img src="https://img.shields.io/badge/docker-ghcr.io%2Fdanleach--glitch%2Fflexarr-e8a0bf?labelColor=1b1720" alt="Docker image"></a>
  <a href="https://apps.apple.com/app/flexarr/id6826618084"><img src="https://img.shields.io/badge/App_Store-iPhone_%26_iPad-f2eee7?logo=apple&logoColor=f2eee7&labelColor=1b1720" alt="On the App Store for iPhone and iPad"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/licence-PolyForm_Noncommercial-f2eee7?labelColor=1b1720" alt="PolyForm Noncommercial 1.0.0"></a>
</p>

<p align="center">
  <a href="https://flexarr.io">flexarr.io</a> ·
  <a href="https://flexarr.io/guide">Guide</a> ·
  <a href="#-quick-start">Quick start</a> ·
  <a href="CHANGELOG.md">Changelog</a> ·
  <a href="docs/roadmap.md">Roadmap</a>
</p>

<p align="center">
  <img src="site/shots/hero.webp" width="900" alt="Flexarr on an Apple TV showing Ted Lasso's page, and an iPhone beside it showing Home.">
</p>

Flexarr is a media server you run at home. It organises your library, fetches artwork and details from the \*arr apps you already run, and plays to your screens: as the file is when the device can manage it, converted on the fly when it can't. There's no account and no cloud in the middle. It runs a real 12,000-file library every day; the [changelog](CHANGELOG.md) has what's new.

## ✨ What it does

| | |
| --- | --- |
| 🎬 **Its own player** on Apple TV, iPhone and iPad: skip intro and credits, Up Next, scrubbing previews | 🌈 **HDR10, HLG and Dolby Vision** reach the TV untouched; **Dolby Atmos** passes through intact |
| ⚡ **Plays files as they are** (AV1, VC-1, XviD and all), converting on the server only when it must | 📺 **Live TV and recording** with a full guide, pause and rewind, from your own tuner or IPTV service through Dispatcharr |
| 🎵 **Music** with synced lyrics, playlists, daily mixes, artist radio, gapless and crossfade | 🎙️ **Podcasts built in**: Apple's directory or any feed, Up Next across devices, OPML import |
| 📥 **Downloads** of films, episodes, albums, playlists and podcasts on iPhone and iPad | 🌍 **Away from home over Tailscale**, no ports opened; the apps switch by themselves |
| 👨‍👩‍👧 **Profiles** with their own history, libraries and PINs | 🗣️ **Siri and Shortcuts**: "Play the next episode of *Ted Lasso* in Flexarr" |
| 🔔 **Notifications**, widgets and the Apple TV Top Shelf | 🏠 **[Home Assistant](https://github.com/danleach-glitch/flexarr-home-assistant)** and an **MCP server** for Claude or any assistant |
| 🔌 **Built on your \*arrs**: metadata from Sonarr, Radarr and Lidarr, requests through Seerr, no TMDB keys | 🧰 **Looks after itself**: library health, Plex history import, backups, readable logs |

<p align="center">
  <img src="site/shots/watch.webp" width="440" alt="An episode paused in the Apple TV player, beside a film's page on an iPhone.">
  <img src="site/shots/live.webp" width="440" alt="The live TV guide on an Apple TV and the channel list on an iPhone.">
</p>

## 📱 Apps

- **iPhone and iPad:** [on the App Store](https://apps.apple.com/app/flexarr/id6826618084).
- **Apple TV:** coming soon to the App Store. Until then, [install it from Xcode](https://flexarr.io/guide/apple-tv-xcode).
- **Web:** built into the server. Open its address in any browser.

Each app connects with a short code you approve in **Settings › Access › Connect a device**; see [Connect your devices](https://flexarr.io/guide/connect-devices).

## 🚀 Quick start

```bash
docker run -d --name flexarr --restart unless-stopped \
  -p 8383:8383 \
  -v flexarr-config:/config \
  -v /path/to/media:/data/media \
  ghcr.io/danleach-glitch/flexarr:latest
```

Open `http://<your-server>:8383` on your home network and choose **Set up Flexarr**: the first browser to reach a new server becomes its owner, no key needed. Then add your folders in **Settings › Libraries** and your \*arrs in **Settings › Connections**. Mount your media at **the same path** Sonarr, Radarr and Lidarr use.

Step-by-step guides on [flexarr.io/guide](https://flexarr.io/guide):

- **Install:** [Linux and Windows (Docker)](https://flexarr.io/guide/install-docker) · [Synology](https://flexarr.io/guide/install-synology) · [Unraid](https://flexarr.io/guide/install-unraid) · [macOS, natively](https://flexarr.io/guide/install-macos)
- **Set up:** [First setup](https://flexarr.io/guide/first-setup) · [Connect your \*arrs](https://flexarr.io/guide/connect-arrs) · [Media paths](https://flexarr.io/guide/media-paths) · [Away from home](https://flexarr.io/guide/away-from-home)
- **Help:** [Troubleshooting](https://flexarr.io/guide/troubleshooting) · [FAQ](https://flexarr.io/guide/faq)

The guide's source is in [docs/guide](docs/guide); corrections are welcome.

## 🛠️ Development

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 24+, FFmpeg 7+, and for the Apple clients Xcode 26+ with [XcodeGen](https://github.com/yonaskolb/XcodeGen).

```bash
make setup     # restore .NET + npm dependencies
make dev-api   # server on http://localhost:8383 (hot reload)
make dev-web   # web client on http://localhost:5173
make test      # server + web tests
make apple     # build the tvOS and iOS apps
make guide     # rebuild flexarr.io/guide from docs/guide
```

| Path | What |
| --- | --- |
| `src/Flexarr.Api` | ASP.NET Core server: REST API, playback sessions, live TV, podcast feeds, background workers |
| `src/Flexarr.Core` | Domain model, database, parsing, library scanning |
| `src/Flexarr.Metadata` | Metadata from Sonarr, Radarr, Lidarr and TVmaze, image cache |
| `src/Flexarr.Streaming` | Playback decisions, FFmpeg/HLS, subtitles, previews, intro/credits detection |
| `src/Flexarr.Integrations.Arr` | Sonarr, Radarr, Lidarr and Seerr clients and webhooks |
| `frontend/` | Web client (React, TypeScript, Vite) |
| `clients/apple/` | `FlexarrKit`, shared app code, and the tvOS and iOS apps |
| `site/` | flexarr.io, with the guide built into `site/guide` |

See [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/architecture.md](docs/architecture.md). What's next is in the [roadmap](docs/roadmap.md).

## 📄 Licence

Flexarr is free to use, change and share for any noncommercial purpose under the [PolyForm Noncommercial License 1.0.0](LICENSE). That covers personal use, home servers, and charities, schools and other noncommercial organisations. You may not sell Flexarr, or use it in a commercial product or service, without a separate licence from the author.

Because it restricts commercial use, Flexarr is *source-available* rather than open source in the [OSI's sense](https://opensource.org/osd).

## 🙏 Acknowledgements

Flexarr is built on many other projects: the \*arr apps it works with, FFmpeg, AetherEngine and more. They're all credited, with their licences, in [ACKNOWLEDGEMENTS.md](ACKNOWLEDGEMENTS.md).
