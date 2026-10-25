# Parity with a leading Apple TV client

A comparison (October 2026, Flexarr 0.19.0) against the feature grid of a well-known third-party Apple TV client
for Plex, and what each gap would take. That client is a player over a Plex server, so some of its rows are the
server's work; here both halves are ours.

✅ have it · 🟡 partly · ❌ not yet

| | Feature | Flexarr today | To reach parity |
|:-:|---|---|---|
| ✅ | Direct play | Every file plays on the device through the engine (Settings › Play files directly, off by default): H.264 and HEVC in hardware, AV1, VP9, MPEG-2, VC-1 and MPEG-4 Part 2 in software, in Flexarr's own player (0.20.0). | Nothing needed. |
| 🟡 | HDR and Dolby Vision | With the engine on: HDR10, HLG, Dolby Vision profiles 5 and 8.1 play as the file is. Profile 7 is converted. Engine off: SDR and profile 5 play as they are; the rest is converted to SDR because tvOS refuses our `hvc1` + PQ masters (see transcode notes). | Profile 7 as its HDR10 base layer: tell the engine to strip the enhancement layer (it has a `StripDolbyVision`-style option) or have the server repackage it as plain HEVC HDR10 once the HDR10 refusal is solved. Small once that's understood. Making HDR work without the engine means solving the refusal; unknown effort. |
| 🟡 | Audio and HomePod | AAC, AC-3 and E-AC-3 with Atmos pass through; DTS, DTS-HD and TrueHD are bridged to Dolby Digital Plus, or to multichannel PCM with Lossless surround on; FLAC and ALAC through the server. HomePod as the Apple TV's output is tvOS's own feature. | Now Playing control from other Apple devices: `MPNowPlayingInfoCenter` and `MPRemoteCommandCenter` with the title, artwork and position, which AVPlayerViewController only partly fills on tvOS; the engine path needs it done by hand. Small. TrueHD Atmos with height channels: the engine's `TrueHDObjectAudio` trait, opt-in; evaluate on a receiver. |
| ❌ | Live TV | None. | A source model (M3U + XMLTV first, then HDHomeRun), a channel guide, an ffmpeg-fed HLS session per viewer with a timeshift buffer, an Apple TV What's On screen. Large: the biggest item on this list. Roadmap's "Later". |
| ❌ | Multiview and recording | None. | Recording: a scheduler over the Live TV sessions writing to a library folder, series rules, EPG matching. Multiview: up to four AVPlayers in a grid with one unmuted. Both depend on Live TV; large. |
| 🟡 | Subtitles | SRT and ASS/SSA as text; PGS and DVB drawn by the engine or burned in by the server; delay and size controls. Delay is remembered per title, size per profile, and the audio and subtitle language picked carry to the next title. | Teletext (`dvb_teletext`): the engine decodes it; expose it as a picture-subtitle track. Small. Subtitle height (vertical offset) as a setting, remembered per profile: small. ASS styling kept rather than flattened: depends on the engine's renderer; medium. |
| ❌ | Content filtering | None. | Import EDL/MCF lists from a URL per title (matched by title or ID), then mute (set the player volume to 0 for the span) or skip (seek past) at the marked times; a Settings switch per profile. Medium: it reuses the marker/auto-skip machinery. |
| ✅ | Skip markers | Intro, recap and credits detected by our server, with auto-skip and Play next. | Nothing needed. An optional community database would only matter for titles our detector misses; low priority. |
| 🟡 | Discover and Watchlist | Trending and popular from Seerr with one-click requests, details for titles you don't have. | A watchlist: a per-profile list of TMDB IDs, with "you already have this" and "requested" badges, its own tab on the TV and the web. Small to medium. |
| ❌ | Insights | Cast on the title page. | Cast and trivia while playing: a transport-bar menu (or swipe-down panel on tvOS) listing the cast with photos from our image cache, plus a TVmaze/TMDB "trivia" pull. Medium; the data is already there for the cast half. |
| 🟡 | Music | Artists, albums, a persistent player on the web. No lyrics, playlists or visualizer; the Apple apps don't play music yet. | Playlists (roadmap "Later"); lyrics from embedded `LYRICS`/`USLT` tags and `.lrc` sidecars, synced when timestamps exist; a Now Playing screen on the Apple apps with artwork, and a visualizer from the audio tap. Medium overall, lyrics small. |
| ✅ | Top Shelf and profiles | Continue Watching in Top Shelf as posters with progress; household profiles with PINs and switching on the TV. | Full-bleed artwork with the title logo as an alternative Top Shelf layout: logos need a source (Sonarr/Radarr images or TMDB `logos`). Small. |

## Suggested order

1. Subtitle height and teletext; Now Playing remote control (small, rounds out playback)
2. Watchlist
3. Insights while playing
4. Music: lyrics, playlists, Apple apps
5. Content filtering
6. Live TV, then recording and multiview
