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

The server has one API key, generated on first run and stored in `<DataDirectory>/config.json`. Send it \*arr-style, as `X-Api-Key: <key>` or `?apikey=<key>`. It is for setting things up, not for every device to keep:

- **Browsers** sign in once with the key (`POST /session`) and get an HttpOnly cookie holding a random session token of their own. **Apple TVs** pair with a code, approved in the web app, and get a device token of their own (sent as `X-Api-Key`); TVs set up with the key itself swap it for one (`POST /session/device`). Only hashes of these tokens are kept, in `<DataDirectory>/devices.json`. Settings › Server lists them with Sign out; `DELETE /session` signs out the one asking. Browsers unused for six months are signed out. The key can be replaced from Settings without signing these out.
- **Pairing** only starts from the home network or tailnet, at most three waiting codes per address, and the approval page shows where the request came from.
- **Profiles.** Only the main profile manages the server (settings, the API key, connecting TVs, deleting media). A browser or TV keeps the profile chosen on it on the server, so another profile there can't regain admin by dropping the `X-Flexarr-Profile` header; the main profile must be chosen again, with its PIN. The API key itself is always admin. PIN attempts are taken one at a time per profile; wrong ones lock out the caller for a while, doubling each time up to an hour, and only throttle the profile itself (one attempt every five seconds), so a guesser can't lock the owner out. Callers that may already manage the server skip that throttle. Strikes are forgotten after half an hour without a wrong PIN. A Tailscale account linked to another profile can't choose a main profile that has no PIN. Showing or replacing the API key asks for the main profile's PIN when it has one (otherwise an explicit confirmation); replacing it can also sign out every other browser and device. Before per-device sign-ins, browsers shared one cookie and profile tokens ignored the PIN; those are accepted until `legacySignInsUntil` (30 days after the first run of the upgrade).
- **Webhooks** use their own secret (`?secret=`), which can only queue scans; `?apikey=` still works.
- **`AuthenticationRequired`** can be `Enabled` or `DisabledForLocalAddresses`. With the latter, requests from the home network sign in as the main profile without a key, but only when addressed to one of this server's names (an IP address, a single-word or `.local`-style name, this machine's name, or one listed in `knownHosts`), so a web page can't use DNS rebinding to get in through a browser.
- **Tailscale.** Behind `tailscale serve`, the `Tailscale-User-Login` header signs in as the profile linked to that account, once `tailscale whois` confirms the forwarded address belongs to it (one shared lookup per address, a minute's cache). Another program on the Mac could still forge that header with a real tailnet address, so a Tailscale identity alone never manages the server: that also takes the main profile chosen with its PIN, or the browser's own API-key sign-in.
- **Pairing endpoints** (`POST /pairing`, `GET /pairing/{id}`) also need a Host this server goes by, like the home-network rule.
- **Network zones.** Traffic through Cloudflare Tunnel or Tailscale Funnel is always the internet. Forwarded requests count as the internet unless they come from this machine or a proxy listed in `trustedProxies`. With `loopbackNeedsForwarding`, a request from this machine without `X-Forwarded-For` counts as the internet too.
- **Other websites.** Writes signed in by cookie, the home network or Tailscale must come from Flexarr's own pages (`X-Requested-With`, `Sec-Fetch-Site: same-origin` or a matching `Origin`). Requests with a key are exempt.

Over plain HTTP the key, the sign-in and the cookie cross the network unencrypted. That's fine on a home network you trust; anywhere else, reach Flexarr through Tailscale (which encrypts and gives it an HTTPS address) or an HTTPS proxy.
