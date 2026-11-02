# Music discovery and requests

Find music the household doesn't have yet and add it through Lidarr. Seerr, which handles film and show requests,
has no music, so music requests go straight to Lidarr. Suggestions come from Deezer's public API, which needs no key.

## What people get

- **Discover › Music** (web, Apple TV, Android TV): rows of *Like artists you play*, *New from your artists*,
  *Top charts*, and genre tiles that open a genre's artists. Search covers artists and albums beyond the library.
- **Search** on the phones (iPhone, Android): artists and albums under *Not in your library*, as films and shows are.
- **Outside artist and album pages**: photo, albums (each with **Request album**), **Follow artist**, tracks, and
  *Fans also like*.
- **Library artist pages** end with *Not in your library* (albums Lidarr lists for the artist that the library lacks)
  and *Fans also like*.
- **Your album is ready**: the person who asked is told when it's in the library.
- **New music goes to**: a per-profile library choice, shown only when the profile can see more than one music library.

Who: the same profiles as film and show requests (no age limit; family guests included). Kids and age-limited
profiles never see any of it: the endpoints sit in the `/requests` group, which is `NotForAgeLimited`, and each checks
that the profile can see a music library.

## What a request does in Lidarr

- **Request album**: if Lidarr doesn't have the artist, add it with `monitored: true`, `monitorNewItems: none` and
  `addOptions: {monitor: none, searchForMissingAlbums: false}`, so nothing else downloads. Then monitor the album
  (`PUT album/monitor`) and search for it (`AlbumSearch`). If Lidarr has the artist already, only the last two.
- **Follow artist**: add the artist with `monitorNewItems: new` and `addOptions: {monitor: latest,
  searchForMissingAlbums: true}`, or set `monitorNewItems: new` on the artist Lidarr has and monitor and search their
  latest studio album. Older albums stay unmonitored and can be requested one by one.
- **Matching**: Deezer and MusicBrainz share no ids, but both know an album's barcode. A Deezer album's UPC finds its
  MusicBrainz release (`release?query=barcode:…`), whose release group and credited artist Lidarr looks up by id
  (`lidarr:<mbid>`); an artist is found through the barcode of one of their releases. Names are only the fallback. MusicBrainz
  allows one request a second (`MusicCatalog` paces and caches them).
- **Profiles**: the quality and metadata profiles are the root folder's defaults (`defaultQualityProfileId`,
  `defaultMetadataProfileId`), falling back to the ones most of that Lidarr's artists use, then the first.
- **Root folder**: the profile's *New music goes to* library, matched to a Lidarr root folder through the path mappings
  (as `RequestDestinations` does for Seerr), else the first root folder. With several Lidarr connections, the one whose
  root folder matches wins; otherwise the first.
- **Off switch**: `MusicRequests.Enabled` (on by default) in config.json, shown as *Allow music requests* with the Lidarr
  connections. Music requests are on only when a Lidarr is connected and this is on.

## Matching Deezer to MusicBrainz

Deezer ids drive what's shown; Lidarr wants MusicBrainz ids. They're matched when someone opens an outside page or
requests:

- **Artist**: `artist/lookup?term=<name>` and take the result whose normalised name equals Deezer's (case, accents,
  a leading "The", "&"/"and" ignored). Several equal names: the one whose album titles overlap Deezer's most. No match:
  not requestable ("Lidarr doesn't know this artist").
- **Album**: `album/lookup?term=<artist> <title>` kept to the matched artist's results (or Lidarr's own album list when
  it has the artist), by normalised title (edition suffixes like "(Deluxe)" and "- Remastered" dropped) and then nearest
  release year.
- **Owned**: an artist is in the library when a music library artist has its MusicBrainz id or, failing that, its
  normalised name. An album is when that artist has an album with the release group id or the normalised title. An
  album Lidarr already monitors counts as **Requested**.

## API (all new; nothing existing changes shape)

Older apps read `tmdbId` as required and treat anything that isn't a show as a film, so music never goes into
`/requests/discover`, `/requests/search` or `ExternalTitleDto`.

| Endpoint | What |
| --- | --- |
| `GET /requests/music/status` | `{configured, reachable}`: false for age-limited profiles and profiles with no music library |
| `GET /requests/music/discover` | Rows of `MusicExternalDto` plus `genres` tiles |
| `GET /requests/music/genres/{id}` | A genre's artists |
| `GET /requests/music/search?q=` | `{artists, albums}` |
| `GET /requests/music/artist/{deezerId}` | `MusicArtistDetailDto`: photo, albums with availability, similar, followed, library id |
| `GET /requests/music/album/{deezerId}` | `MusicAlbumDetailDto`: cover, year, tracks, availability, library id |
| `POST /requests/music` | `{kind: "album" \| "artist", deezerId \| musicBrainzId \| artistId}` → 202; 403 hidden music; 409 off or unmatched; 502 Lidarr or Deezer down |
| `GET /requests/music/library-artist/{itemId}` | For a library artist: `missing` albums (Lidarr's list, else Deezer's), `similar` (Deezer), `followed`, `deezerId` |
| `GET /requests/music/destinations` | The music libraries requests can go to, for *New music goes to* |
| `PUT /profiles/{id}/request-music-library` | `{libraryId}`; the profile itself or the admin |

`MusicExternalDto` is `{kind: "artist" | "album", deezerId, title, artist, year, image, availability, libraryItemId}`.
`availability` reuses `AvailabilityDto` (None, Requested, PartlyAvailable, Available). The profile DTO gains
`requestMusicLibraryId`. Music destinations have their own endpoint rather than joining `/requests/destinations`,
whose readers only expect `movie` and `tv`. Everything under `/requests/music` answers 409 when no Lidarr is connected
or the switch is off, and 403 to a profile that sees no music library.

## Data

One new migration: `MediaRequest.MusicBrainzId` (nullable; the release group for an album, the artist for a follow)
with `Kind` `Album` or `Artist` and `TmdbId` 0, and `Profile.RequestMusicLibraryId`. The notification worker fulfils an
album request when an album with that release group (or the artist's album with the requested title) is scanned in,
and a follow when any album of the artist's arrives after the request.

## Deezer

`DeezerClient` in `Flexarr.Integrations.Arr` (beside the other outside services): search, artist, artist albums,
related artists, album with tracks, charts, genres and genre artists. Answers are cached in memory (charts and genres
6 hours, artists and albums a day) and calls have a short timeout; Deezer's unpublished limit is about 50 requests per
5 seconds. Its image host `cdn-images.dzcdn.net` is trusted by `ArtworkUrl`, so covers go through the image cache.
Discover › Music carries a small *Data from Deezer* note.

## Clients

Web (`DiscoverPage`, new `MusicExternalPages`, `MusicPages` artist extras, settings); FlexarrKit `MusicRequests.swift`
with iPhone (Search, outside pages, artist extras) and Apple TV (Discover's Music mode, outside pages, artist extras);
Android `core/MusicRequests.kt` with the phone and Android TV equivalents. Every app treats a 404 from the status
endpoint as off, so they work with servers from before this.

## Following, undoing and deleting (music-polish)

- **Followed artists join the library at once.** Following an artist the library doesn't have adds a *placeholder*
  artist (`MediaItem.Placeholder`) to the profile's *New music goes to* library, with their name, MusicBrainz id and
  photo. Scans leave a placeholder alone (it's neither marked missing nor pruned), and the scanner reuses it by name when
  their first album arrives, which clears the flag. Unfollowing removes a placeholder that's still empty.
- **Unfollow** (`POST /requests/music/unfollow`, `{artistId | deezerId | musicBrainzId}`) is household-wide, as Lidarr
  is shared: Lidarr's artist keeps `monitored: true` (so albums already asked for still download) with
  `monitorNewItems: none`. Every profile's follow row for the artist closes.
- **Requested** (`GET /requests/music/requested`): every album Lidarr is waiting for in folders the profile can see:
  monitored albums without files (upcoming ones with their release date), plus open Flexarr album requests Lidarr
  doesn't list yet. Each carries Lidarr's queue state when it's downloading (`Downloading` with progress, `Importing`),
  else `Searching`, or `Upcoming` before its release date.
- **Unrequest** (`POST /requests/music/unrequest`, `{musicBrainzId}` = the release group): unmonitors the album in
  Lidarr, removes it from Lidarr's queue, and closes every profile's request for it. A request that added its artist to
  Lidarr (`MediaRequest.AddedArtist`) takes the artist out again when nothing else of theirs is monitored, followed or
  on disk. Any profile that can request music can unrequest.
- **Delete** (`DELETE /items/{id}` for albums and artists, main profile only, web only for now). With Lidarr managing
  it: an artist is deleted with their files (no import list exclusion); an album's track files are deleted and the
  album unmonitored, so it stays requestable but doesn't download again. Without Lidarr, Flexarr deletes the files as
  it does for films. An artist an album delete leaves empty goes too, unless they're followed (then a placeholder).
- **Undo in the apps:** pressing Following or Requested asks once (*Stop following …?*, *Cancel request?*).
- **One action order** for artist and album pages: Play, Shuffle, Radio, Download, Follow/Request, ⋯ (`MusicActions`
  in FlexarrKit and Android `core`). The web labels each button; the apps show icons.
- Everything here hides when no Lidarr is connected or music requests are off (`status.configured`), and greys with
  *Lidarr isn't answering* when it doesn't answer (`status.reachable`).

## Later

30-second previews, download progress from Lidarr's queue, an MCP tool to request by voice, telling the admin when
someone requests.
