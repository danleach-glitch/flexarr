# Acknowledgements

Flexarr stands on a lot of other people's work. Thank you to everyone behind the projects below. Each one keeps its own licence; Flexarr's [licence](LICENSE) covers only Flexarr's own code.

## The *arr family and friends

Flexarr was built to sit alongside these apps, and follows their conventions so their users and contributors feel at home. It talks to them over their APIs and includes none of their code.

- [Sonarr](https://sonarr.tv), [Radarr](https://radarr.video) and [Lidarr](https://lidarr.audio): TV, films and music, and most of Flexarr's metadata
- [Seerr](https://github.com/seerr-team/seerr): requests and Discover
- [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr): Live TV

Flexarr isn't affiliated with or endorsed by any of them. The web app shows each app's logo, taken from its own repository: Sonarr's, Radarr's and Lidarr's under the GPL-3.0, Seerr's under the MIT licence, and Dispatcharr's from its web app. The names and logos belong to their projects.

## Server

| Project | Used for | Licence |
|---|---|---|
| [.NET](https://dotnet.microsoft.com) and [ASP.NET Core](https://github.com/dotnet/aspnetcore) | The server | MIT |
| [Entity Framework Core](https://github.com/dotnet/efcore) | Database access | MIT |
| [SQLite](https://sqlite.org) | The database | Public domain |
| [Scalar](https://github.com/scalar/scalar) | API reference pages | MIT |
| [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) | The MCP endpoint | Apache-2.0 / MIT |
| [Serilog](https://serilog.net) | Logs | Apache-2.0 |
| [Sentry for .NET](https://github.com/getsentry/sentry-dotnet) | Error reports, only when turned on | MIT |
| [FFmpeg](https://ffmpeg.org) | Transcoding, remuxing, previews, audio fingerprints | LGPL-2.1+ / GPL-2.0+ depending on the build. Flexarr runs it as a separate program and doesn't include it, except in the Docker image, which installs Debian's package, and the Mac app (in development), which includes an LGPL-2.1+ build of the unmodified [FFmpeg 9.0.2 source](https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz), configured by [clients/macos/Tools/build-ffmpeg.sh](clients/macos/Tools/build-ffmpeg.sh). |

## Web app

| Project | Used for | Licence |
|---|---|---|
| [React](https://react.dev) | The interface | MIT |
| [React Router](https://reactrouter.com) | Navigation | MIT |
| [hls.js](https://github.com/video-dev/hls.js) | Streaming playback | Apache-2.0 |
| [Sentry for React](https://github.com/getsentry/sentry-javascript) | Error reports, only when turned on | MIT |
| [Atkinson Hyperlegible Next](https://www.brailleinstitute.org/freefont/) by the Braille Institute, via [Fontsource](https://fontsource.org) | Body text | SIL OFL 1.1 |
| [Big Shoulders Display](https://github.com/xotypeco/big_shoulders) by Patric King, via Fontsource | Display type | SIL OFL 1.1 |

## Apple TV, iPhone and iPad apps

| Project | Used for | Licence |
|---|---|---|
| [AetherEngine](https://github.com/superuser404notfound/AetherEngine) by Vincent Herbst | The on-device player | LGPL-3.0 with an App Store exception |
| [FFmpegBuild](https://github.com/superuser404notfound/FFmpegBuild): [FFmpeg](https://ffmpeg.org), [dav1d](https://code.videolan.org/videolan/dav1d), [zimg](https://github.com/sekrit-twc/zimg) and [libzvbi](https://github.com/zapping-vbi/zvbi) | Demuxing and decoding, as dynamic frameworks | LGPL-2.1+, BSD-2-Clause, WTFPL, LGPL-2.0+ |
| [LibDovi](https://github.com/superuser404notfound/LibDovi), around [dovi_tool](https://github.com/quietvoid/dovi_tool) | Dolby Vision metadata | MIT |
| [LibTrueHD](https://github.com/superuser404notfound/LibTrueHD), around [truehd](https://github.com/truehdd/truehdd) | Dolby TrueHD and Atmos decoding | MIT |
| [SMBClient](https://github.com/kishikawakatsumi/SMBClient) by Kishikawa Katsumi | Reading files from network shares | MIT |
| [Sentry for Apple platforms](https://github.com/getsentry/sentry-cocoa) | Error and crash reports, only when turned on | MIT |
| [XcodeGen](https://github.com/yonaskolb/XcodeGen) | Generating the Xcode project | MIT |

## Development

[Vite](https://vite.dev), [Vitest](https://vitest.dev), [TypeScript](https://www.typescriptlang.org), [Testing Library](https://testing-library.com), [jsdom](https://github.com/jsdom/jsdom), [Oxlint](https://oxc.rs), [xUnit](https://xunit.net) and [Coverlet](https://github.com/coverlet-coverage/coverlet), all MIT or Apache-2.0.

## Data and services

- **[TMDB](https://www.themoviedb.org).** Film and TV artwork comes from TMDB's image service, and films Radarr doesn't know can be looked up with your own free TMDB key. This product uses the TMDB API but is not endorsed or certified by TMDB.
- **[TheTVDB](https://thetvdb.com).** TV metadata and artwork, by way of Sonarr.
- **[TVmaze](https://www.tvmaze.com).** TV schedules, cast and episode details, and shows Sonarr doesn't know, from its [API](https://www.tvmaze.com/api), under CC BY-SA 4.0.
- **[fanart.tv](https://fanart.tv).** Logos and artwork, by way of the *arr apps.
- **[MusicBrainz](https://musicbrainz.org).** Music metadata, by way of Lidarr.
- **[Apple's iTunes Search API](https://performance-partners.apple.com/search-api).** Finding podcasts.
- **[Podcast Index](https://podcastindex.org).** Its podcast namespace adds chapters, transcripts and more to feeds.
- **[Kodi](https://kodi.tv)'s `.nfo` format.** Flexarr reads the `.nfo` files and artwork that Kodi, Jellyfin, Emby and tinyMediaManager write beside your files.
- **[Sentry](https://sentry.io).** Receives error reports, only if a server's owner turns them on in Settings › Logs.

## Demo and screenshots

The App Review demo server and some screenshots use the [Blender Foundation's open films](https://studio.blender.org/films/), © Blender Foundation, licensed under [CC BY](https://creativecommons.org/licenses/by/3.0/).

## Contributors

Thank you to everyone who has [contributed](https://github.com/danleach-glitch/flexarr/graphs/contributors) code, bug reports and ideas.

---

Something missing or wrong here? [Open an issue](https://github.com/danleach-glitch/flexarr/issues) and it'll be fixed.
