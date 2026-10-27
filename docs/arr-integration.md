# \*arr integration

Flexarr is designed to be the playback end of an \*arr stack: the \*arrs find and organise media, Flexarr serves it.

## Today: webhooks

Each \*arr app sends a webhook when it imports, upgrades, renames or deletes media. Flexarr rescans only the affected folder.

| App | Webhook URL | Folder that gets rescanned |
| --- | --- | --- |
| Sonarr | `/api/v1/webhooks/sonarr?secret=SECRET` | `series.path` |
| Radarr | `/api/v1/webhooks/radarr?secret=SECRET` | `movie.folderPath` |
| Lidarr | `/api/v1/webhooks/lidarr?secret=SECRET` | `artist.path` |

`SECRET` is the webhook secret from Settings › Connections, which shows each address filled in (`webhookSecret` in `config.json`). It can only queue scans, so the API key needn't live in the \*arr apps' settings. Webhooks set up with `?apikey=KEY` keep working.

Events that trigger a scan: `Download` (import/upgrade), `Rename`, `SeriesAdd`/`MovieAdded`/`ArtistAdd`, `*Delete`, `AlbumDownload`, `TrackRetag`. `Test` returns `200 OK`; other events (`Grab`, `Health`, …) are accepted and ignored.

**Paths.** Mount media at the same path in Flexarr and the \*arrs (the provided `docker-compose.yml` uses `/data/media` everywhere). Where they differ, such as \*arrs in Docker and Flexarr on the Mac, add `pathMappings` to `config.json` (see [install-macos.md](install-macos.md)).

## Connections

Sonarr, Radarr, Lidarr and Seerr are added in **Settings › Connections** with an address and API key. Flexarr uses them for:

- **Metadata and artwork.** Everything Sonarr, Radarr and Lidarr know about a title, matched by folder, then IMDb, TMDB or TVDB id, with no keys of Flexarr's own. Titles they don't manage are found through their lookup endpoints.
- **Coming soon**, from Sonarr's and Radarr's calendars.
- **Collections** of film series, from Radarr.
- **Library health** and **delete**: "find a better copy" searches, and deleting a title through Sonarr or Radarr so it isn't downloaded again.
- **Discover and requests** through Seerr: trending and popular titles, ratings, and Request, with "your request is ready" notifications when it arrives.

Dispatcharr, for live TV, is added in the same place; see [architecture.md](architecture.md#key-flows).

## Ideas

- **Native "Connect" support.** Emulate the Emby/Jellyfin library-refresh endpoint so the \*arrs' existing *Emby / Jellyfin* connection works without a webhook, and propose a native Flexarr connection upstream.
- **Watched-state sync** back to the \*arrs (e.g. for "unmonitor when watched" workflows).
