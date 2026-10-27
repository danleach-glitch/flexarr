<p align="center">
  <img src="site/logo-mark.svg" width="72" alt="">
</p>

<h1 align="center">Flexarr</h1>

<p align="center"><strong>Your entertainment. Every screen.</strong></p>

<p align="center">
  A free, source-available, self-hosted media server for your films, TV shows, music and podcasts, built to sit alongside <a href="https://sonarr.tv">Sonarr</a>, <a href="https://radarr.video">Radarr</a> and <a href="https://lidarr.audio">Lidarr</a>.<br>
  Stream your library to the web, Apple TV, iPhone and iPad. No account. No cloud.
</p>

<p align="center">
  <a href="https://github.com/danleach-glitch/flexarr/releases"><img src="https://img.shields.io/github/v/release/danleach-glitch/flexarr?display_name=tag&color=a9c2ec&labelColor=1b1720" alt="Latest release"></a>
  <a href="https://github.com/danleach-glitch/flexarr/actions/workflows/ci.yml"><img src="https://github.com/danleach-glitch/flexarr/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/danleach-glitch/flexarr/pkgs/container/flexarr"><img src="https://img.shields.io/badge/docker-ghcr.io%2Fdanleach--glitch%2Fflexarr-e8a0bf?labelColor=1b1720" alt="Docker image"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/licence-PolyForm_Noncommercial-f2eee7?labelColor=1b1720" alt="PolyForm Noncommercial 1.0.0"></a>
  <img src="https://img.shields.io/badge/.NET%2010%20%C2%B7%20React%20%C2%B7%20Swift-a39aae?labelColor=1b1720" alt=".NET 10, React, Swift">
</p>

<p align="center">
  <a href="https://flexarr.io">flexarr.io</a> ·
  <a href="#-quick-start-docker">Quick start</a> ·
  <a href="#-apple-tv-iphone-and-ipad">Apple apps</a> ·
  <a href="CHANGELOG.md">Changelog</a> ·
  <a href="docs/roadmap.md">Roadmap</a>
</p>

<p align="center">
  <img src="site/shots/web-home.jpg" width="900" alt="The web app's Home page: a featured show with Resume and More info, and the Continue watching row beneath.">
</p>

Flexarr is a media server you run yourself. It organises your library, fetches artwork and details, and plays to your screens, as the file is when the device can manage it and converted on the fly when it can't. There's no account and no cloud in the middle. It's built for people who already run the \*arr stack, so Sonarr, Radarr, Lidarr and Seerr are where the metadata, the artwork and the requests come from. Running a real 12,000-file library day to day; see the [changelog](CHANGELOG.md) for what's new.

## ✨ Highlights

| | |
| --- | --- |
| 🎬 **Its own player** on Apple TV, iPhone and iPad: next and previous episode, skip intro and credits, scrubbing previews, scan at 2× to 16× | 🌈 **HDR10, HLG and Dolby Vision** reach the TV untouched; **Dolby Atmos** passes through intact |
| ⚡ **Plays files as they are** on the device (AV1, VC-1, XviD and all), with hardware transcoding on the server when needed | 🎵 **Music like a streaming app**: queue, playlists, lyrics, daily mixes, artist radio, gapless and crossfade |
| 👨‍👩‍👧 **Profiles** with their own history, PINs and libraries, and a picker when Flexarr opens | 🗣️ **Siri**: "Play the next episode of *Curb Your Enthusiasm* in Flexarr", "Shuffle *Family Guy* in Flexarr" |
| 📥 **Downloads** of films, episodes, albums and playlists on iPhone and iPad | 🌍 **Away from home over Tailscale**, no ports opened; the apps switch by themselves |
| 🎙️ **Podcasts built in**: follow shows from Apple's directory or by feed address, Up Next shared across devices, speeds, sleep timer, OPML import | 🔔 **New episode alerts** and widgets for Continue listening and Up Next on iPhone and iPad |
| 🔌 **Built on your \*arrs**: metadata from Sonarr, Radarr and Lidarr, requests through Seerr, no TMDB keys | 🤖 **An MCP server**, so Claude or any assistant can search, play and pause on your TV |

## 🍿 Watching

- 🎞️ **Direct play** when the device can, **repackaging** rather than re-encoding H.264 and HEVC, and **hardware-accelerated** conversion for the rest (VideoToolbox, VA-API, Quick Sync, NVENC), seeking anywhere instantly
- 🔊 **Atmos** passed through; TrueHD, DTS-HD and PCM as lossless FLAC up to 7.1, or multichannel PCM over HDMI
- ⏭️ **Skip intro, recap and credits**, found by audio fingerprinting across each season, plus **Up Next** with a countdown
- 💬 **Subtitles** embedded and sidecar, text and picture, with timing and size you can adjust; your choice, off included, sticks for the show
- 📱 Resume everywhere, Picture in Picture, AirPlay, lock-screen controls

## 🔎 Finding something

- 🏠 Home with Continue Watching, Top 10s, genre rows, "Because you watched" and new arrivals, every row movable or off
- ⌘ **Search** across titles, episodes, people and genres, with person pages and Discover by genre
- 🗂️ **Collections**: your own, smart ones that fill themselves from rules, and film series from Radarr
- 🔀 **Shuffle** a show; 📅 **Coming soon** from Sonarr and Radarr; 🙋 **Requests** through Seerr in one click

## 🎧 Music

- 🖼️ Artist pictures, album covers and details from **Lidarr**, with albums, EPs, singles and live records shelved apart
- ▶️ Play, shuffle and **queue** anything; a now-playing bar that follows you; **playlists** per profile
- 🎤 **Lyrics** synced to the song, on the web, Apple TV, iPhone and iPad
- 💿 **Made for you** daily mixes, artist **radio**, recently and most played
- 🎚️ Near-gapless playback, optional **crossfade**, and **volume normalisation** from ReplayGain tags

## 🎙️ Podcasts

Built in, with nothing to set up: Podcasts sits with your libraries, and each profile follows its own shows.

- 🔭 **Browse** Apple's podcast directory by top charts and category, **search** it, or paste any feed's address
- ➕ **Follow** a show and its new episodes land under Podcasts and in a **New podcasts** row on Home; Flexarr checks feeds by itself, more often for shows that publish often
- 📋 **Up Next**: Play next and Add to Up Next on any episode, one reorderable queue shared by the web and your phone
- ▶️ Resume from where you left off on any device, **15 back and 30 on**, speeds from 0.8× to 3×, a **sleep timer**, and the next episode plays by itself
- 🗂️ Unplayed first, **newest or oldest first** per show (for ones heard in order), and **Mark all as played** to clear a back catalogue
- 📲 **Offline on iPhone and iPad**: download any episode, or let the newest of the shows you follow download by themselves on Wi-Fi (or mobile data, if you allow it); played ones are cleared away
- 🔔 **Notifications** for new episodes, with a Play button, and **widgets** for Continue listening and Up Next
- 🔁 **Bring your shows over** from Pocket Casts, Overcast or most other apps with **OPML import**, and export them back
- 🔒 Episodes play through your server, so they work over Tailscale and podcast hosts don't see each listener

## 🧰 Looking after your library

- 📜 **Bring your history over from Plex**, matched by file, without touching Plex
- 🩺 **Library health**: duplicates and the space they use, unreadable files, standard-definition titles with a "find a better copy" button
- 🛠️ **Fix match** for a wrongly matched title, and **delete** through Sonarr and Radarr from a title's page

## 📺 Apple TV, iPhone and iPad

The Apple apps (in `clients/apple`) have Home, your libraries, search, title pages, profiles, music, podcasts (iPhone and iPad) and Flexarr's own player, with every setting following the profile from device to device.

- 📺 **Apple TV:** Top Shelf with Continue Watching, a Now Playing tab while music plays, Settings and Switch profile under your picture
- 📱 **iPhone and iPad:** Picture in Picture, AirPlay, lock-screen and Control Center, **Downloads** for offline, and **podcasts** with Up Next, widgets and new-episode notifications
- 🚗 **CarPlay** for podcasts (Up Next, New episodes and your shows), switched on once Apple approves Flexarr for CarPlay
- 🔐 Both find your server on the home network; approve the code the app shows in **Settings › Access › Connect a device** on a signed-in browser, and each device gets a sign-in of its own

**Getting them:** the iPhone and iPad app is [on the App Store](https://apps.apple.com/app/flexarr/id6826618084). The Apple TV app installs from Xcode onto your own device:

1. Sign Xcode in to your Apple ID (Xcode › Settings › Accounts). A free one works.
2. Pair the Apple TV: on the TV, Settings › Remotes and Devices › Remote App and Devices; on the Mac, Devices and Simulators (⇧⌘2).
3. Run `make apple-install TEAM=<your team id> BUNDLE_ID=com.yourname.flexarr.tv`.

For an iPhone or iPad from Xcode, turn on Developer Mode on the device and run `make ios-install TEAM=<your team id> BUNDLE_ID=com.yourname.flexarr.ios`. With a free Apple ID the apps stop opening after 7 days; run the command again.

### 🗣️ Siri and Shortcuts

Once the iPhone or iPad app has signed in, Siri understands, with no setting up:

- "Play **the next episode of** *Curb Your Enthusiasm* in Flexarr": a show plays the episode you're up to, a film resumes
- "**Shuffle** *Family Guy* in Flexarr": random episodes, one after another
- "Continue watching in Flexarr", "Pause Flexarr", "Resume Flexarr", "Stop Flexarr"
- 🎙️ "Play *The Rest Is History* in Flexarr" plays the episode you'd hear next; also "Play the latest …", "What's new in my podcasts", "Add … to Up Next" and "Play my Up Next"

Playback lands on the iPhone unless **Settings › Siri** names an Apple TV, or you name one as you ask ("… on the living room TV"). The Apple TV takes commands while Flexarr is open on it. The same actions are in the Shortcuts app.

### 🤖 An assistant (MCP)

Flexarr is an [MCP](https://modelcontextprotocol.io) server at `http://<server>:8383/api/v1/mcp`, signed in with the API key as an `X-Api-Key` header. An assistant can search the library, see Continue Watching and what's playing on every screen, play, pause or stop on any device with Flexarr open, and list your podcasts' new episodes, queue them in Up Next or play one. For Claude Code:

```bash
claude mcp add --transport http flexarr http://mini.local:8383/api/v1/mcp --header "X-Api-Key: <your key>"
```

## 🚀 Quick start (Docker)

```bash
docker run -d --name flexarr \
  -p 8383:8383 \
  -v /path/to/config:/config \
  -v /path/to/media:/data/media \
  ghcr.io/danleach-glitch/flexarr:latest
```

`latest` is the newest [release](https://github.com/danleach-glitch/flexarr/releases); pin a version such as `0.27` to upgrade when you choose.

Open <http://localhost:8383> (or the server's address from another computer on your home network) and choose **Set up Flexarr**: the first browser to reach a new server becomes its owner, no key needed. Phones, tablets and Apple TVs then connect with a code you enter in Settings › Access › Connect a device; each gets a sign-in of its own, listed in Settings › Access. The API key in `/config/config.json` is still there for scripts and for connecting from outside your home.

Plain `http://` is fine on a home network you trust. From anywhere else, use Tailscale (Settings › Remote access) or an HTTPS reverse proxy, listed in `trustedProxies` if it runs on another machine. Mount your media at the **same path** Sonarr, Radarr and Lidarr use, or add `pathMappings` to `config.json`. For podcast downloads to phones, choose a folder in **Settings › Podcasts** where episodes wait on their way (each is deleted once the phone has it).

🍎 **On a Mac**, run Flexarr natively for hardware transcoding: `make deploy DEPLOY_HOST=<ssh-host>`. See [docs/install-macos.md](docs/install-macos.md).

## 🔌 Connect Sonarr, Radarr, Lidarr and Seerr

Add each in **Settings › Connections** with its address and API key. Then, so new downloads appear the moment they land, add a webhook in each \*arr (**Settings → Connect → + → Webhook**):

- **URL:** `http://<flexarr-host>:8383/api/v1/webhooks/sonarr?secret=<webhook-secret>` (`radarr` or `lidarr` as appropriate; Settings › Server shows them filled in)
- **Method:** POST
- **Triggers:** On Import, On Upgrade, On Rename, On Delete

See [docs/arr-integration.md](docs/arr-integration.md) for details.

## 🛠️ Development

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 24+, FFmpeg 7+, and for the Apple clients Xcode 26+ with [XcodeGen](https://github.com/yonaskolb/XcodeGen).

```bash
make setup     # restore .NET + npm dependencies
make dev-api   # server on http://localhost:8383 (hot reload)
make dev-web   # web client on http://localhost:5173
make test      # server + web tests
make apple     # build the tvOS and iOS apps
```

| Path | What |
| --- | --- |
| `src/Flexarr.Api` | ASP.NET Core server: REST API, playback sessions, podcast feeds, background workers |
| `src/Flexarr.Core` | Domain model, database, parsing, library scanning |
| `src/Flexarr.Metadata` | Metadata from Sonarr, Radarr, Lidarr and TVmaze, image cache |
| `src/Flexarr.Streaming` | Playback decisions, FFmpeg/HLS, subtitles, previews, intro/credits detection |
| `src/Flexarr.Integrations.Arr` | Sonarr, Radarr, Lidarr and Seerr clients and webhooks |
| `frontend/` | Web client (React, TypeScript, Vite) |
| `clients/apple/` | `FlexarrKit`, shared app code, and the tvOS and iOS apps |

See [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/architecture.md](docs/architecture.md). What's next is in the [roadmap](docs/roadmap.md).

## 📄 Licence

Flexarr is free to use, change and share for any noncommercial purpose under the [PolyForm Noncommercial License 1.0.0](LICENSE). That covers personal use, home servers, and charities, schools and other noncommercial organisations. You may not sell Flexarr, or use it in a commercial product or service, without a separate licence from the author.

Because it restricts commercial use, Flexarr is *source-available* rather than open source in the [OSI's sense](https://opensource.org/osd).

## 🙏 Acknowledgements

Flexarr is built on many other projects: the *arr apps it works with, FFmpeg, AetherEngine and more. They're all credited, with their licences, in [ACKNOWLEDGEMENTS.md](ACKNOWLEDGEMENTS.md).
