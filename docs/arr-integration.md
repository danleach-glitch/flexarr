# \*arr integration

Flexarr is designed to be the playback end of an \*arr stack: the \*arrs find and organise media, Flexarr serves it.

This page is how it works underneath. To set it up, see the guide: [Connect your \*arrs](guide/connect-arrs.md) and [Media paths](guide/media-paths.md).

## Today: webhooks

Each \*arr app sends a webhook when it imports, upgrades, renames or deletes media. Flexarr rescans only the affected folder.

| App | Webhook URL | Folder that gets rescanned |
| --- | --- | --- |
| Sonarr | `/api/v1/webhooks/sonarr?secret=SECRET` | `series.path` |
| Radarr | `/api/v1/webhooks/radarr?secret=SECRET` | `movie.folderPath` |
| Lidarr | `/api/v1/webhooks/lidarr?secret=SECRET` | `artist.path` |

`SECRET` is the webhook secret from Settings › Connections (`webhookSecret` in `config.json`). It can only queue scans, so the API key needn't live in the \*arr apps' settings. Webhooks set up with `?apikey=KEY` keep working.

Events that trigger a scan: `Download` (import/upgrade), `Rename`, `SeriesAdd`/`MovieAdded`/`ArtistAdd`, `*Delete`, `AlbumDownload`, `TrackRetag`. `Test` returns `200 OK`; other events (`Grab`, `Health`, …) are accepted and ignored.

**Paths must match.** Mount media at the same path in Flexarr and the \*arrs (the provided `docker-compose.yml` uses `/data/media` everywhere). Where they can't match, add a path mapping in Settings › Connections.

## Planned

- **Native "Connect" support.** Emulate the Emby/Jellyfin library-refresh endpoint so the \*arrs' existing *Emby / Jellyfin* connection works without configuration, and propose a native Flexarr connection upstream.
- **Server-side \*arr connections** (`Flexarr:Arr` config, `ArrClient`): show upcoming episodes/releases from calendars, the download queue and import status inside Flexarr.
- **Requests.** Let users request a missing film, series or album from the Flexarr UI; Flexarr adds it to Radarr/Sonarr/Lidarr with the configured quality profile and root folder.
- **Metadata hints.** Use the TVDB/TMDB/MusicBrainz IDs in \*arr payloads to match items instantly instead of guessing from filenames.
- **Watched-state sync** back to the \*arrs (e.g. for "unmonitor when watched" workflows).
