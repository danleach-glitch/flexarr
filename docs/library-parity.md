# Library parity: Movies, TV shows, Music, Audiobooks

How the four main kinds of library compare, in the server and in every app, as of 2 November 2026 (after the music
work in #271–#276 and the first three batches of fixes from this page). Work often goes deep on one kind of library at a time; this page says where one kind is now ahead
of the others, so the next brief can carry the idea across, and where the same idea looks or reads differently.

It was mapped from the code, not by using the apps, so a mark can be wrong in a corner. Check before building on one.
Update it when a library gets something new: add the row, mark the others.

✅ there · 🟡 partly · ❌ missing · — doesn't apply.
Apps: **W** web, **i** iPhone/iPad, **tv** Apple TV, **A** Android phone, **ATV** Android TV.

## Library page

| | Movies | TV shows | Music | Audiobooks |
| --- | --- | --- | --- | --- |
| Hero at the top of Browse | ✅ rotating featured, Play/Resume | ✅ Resume S1E3 | ✅ led by the album you played last, Resume from the next track, Radio for an artist | ❌ every app: shelves only |
| Browse rows from the server, per-profile order | ✅ | ✅ | ✅ | ✅ (own shelves endpoint) |
| **See all** on every Browse row | ✅ | ✅ | ✅ *Your artists* and *New albums* | 🟡 ✅ i/A, ❌ tv |
| Top 10, genre rows | ✅ | ✅ | ❌ (*Most played* tracks instead) | ❌ |
| Coming soon row | ✅ Radarr | ✅ Sonarr | ❌ Lidarr has a calendar, unused | — |
| Views at the top | Browse, All, Genres | Browse, All, Genres | Browse, Artists, Albums, Genres, + Playlists and Requested on W | Browse, All books, Genres, Series, Authors; 🟡 tv has no Series/Authors |
| View switch style | segmented (pills on W) | segmented | segmented | segmented (pills on W) |
| Opens-on setting | ✅ tv, ATV | ✅ tv, ATV | ✅ tv; ❌ ATV (always Browse) | 🟡 tv shares the movies/TV setting |
| Sorts | A–Z, Recently added, Year, Rating | same | Artists A–Z, Recently added (+ Year, Rating on W/tv); Albums A–Z, Recently added, Year | A–Z, Author, Series, Recently added, Year, Length, Recently listened; tv has 4 |
| Filters | genre | genre | 🟡 genre on tv/ATV/i; ❌ W, A | ✅ progress, length, unabridged (W only through links; ❌ tv) |
| A–Z rail | ✅ W, tv, ATV | ✅ W, tv, ATV | ✅ W, tv, ATV | ❌ W, tv |
| Search box in the grid (W) | ✅ | ✅ | ✅ | ❌ |
| Back keeps your place (W) | ✅ | ✅ | ✅ | ❌ (pages of 60, place lost) |
| Grid errors with **Try again** | ✅ | ✅ | ✅ | ✅ |
| Pull to refresh (A) | 🟡 not Genres | 🟡 | 🟡 not Genres | 🟡 not All, Series, Authors |
| Android TV | ✅ | ✅ | ✅ | ❌ left off the TV; dead ends say "on your phone" |

## Genres

| | Movies | TV shows | Music | Audiobooks |
| --- | --- | --- | --- | --- |
| Tiles with pictures | ✅ one backdrop | ✅ | ✅ artist art | ✅ fan of three covers |
| What a genre opens | its own page under the genre's name, Back to Genres (**‹ Genres** on W) | same | same, its albums | same, its books |
| Play / Shuffle a genre | — | — | ✅ every app (up to 200 tracks) | — |
| Empty genre wording | "No genres here yet. They come with…" | same | same | same |

## Detail pages

| | Movie / show / episode | Artist / album | Book / series / author |
| --- | --- | --- | --- |
| Shared button model | ✅ `DetailActions` (FlexarrKit, Android core) | ✅ `MusicActions`, artists and albums | ✅ `BookActions`: Play, Start over, Finished, Download (phones), ⋯ |
| ⋯ menu | ✅ every app | ✅ album and artist pages: Play next, Add to queue, Add to playlist (W, phones); Delete W only | 🟡 W: Edit, chapters, Fix match, collections, Delete; i, tv, A: Delete only (main profile); ATV has no book page |
| IMDb / Rotten Tomatoes | ✅ | — | — |
| People | cast and crew → person pages with bios | 🟡 artist bio text only; artists aren't people | ✅ author cards with photo and bio, narrators |
| Similar | *More like this* (not episodes; no Seerr needed) | *Fans also like*, artists only, needs Lidarr; ❌ albums have no *More by* | *More in series*, *More by author*, *Read by* (❌ *Read by* on tv) |
| **Not in your library** | 🟡 only in franchise collections; ❌ missing seasons/episodes | ✅ artist page, every app | ❌ missing books in a series |
| Trailer | ✅ | — | — |
| Layout | full-width backdrop | square cover + glow | square cover + glow |
| Series page Play next | — | — | ✅ W, i, A; 🟡 tv only moves focus |
| Bookmarks | — | — | ✅ i; ❌ tv |

## Requests and Discover

| | Movies / TV (Seerr) | Music (Lidarr) | Audiobooks |
| --- | --- | --- | --- |
| Discover | ✅ W, tv, ATV; ❌ phones | ✅ W, tv, ATV; phones through Search | ❌ (no Readarr, by design since #257) |
| Request button states | 🟡 Request; *Requested* is plain text | ✅ Request album / Follow, with icons | — |
| Take a request back | ❌ | ✅ Unrequest / Unfollow, asks first | — |
| Greyed when the service is down | ❌ | ✅ | — |
| *Can't request* with a reason | 🟡 one case (no TVDB id) | ✅ | — |
| Requested view with progress | 🟡 Coming soon shows download % (whole queue, not your requests) | ✅ W library tab; ❌ apps | — |
| Follow for new releases | ❌ (Sonarr monitoring is the nearest) | ✅ + *New from …* notification | — |
| Search *Not in your library* | ✅ i; 🟡 A opens a menu, not a page; ❌ W, tv, ATV | ✅ i, A open the page; ❌ W, tv, ATV | ❌ |
| Request-ready notification | ✅ | ✅ | — |
| Watchlist that requests | ✅ | ❌ | — |

## Playing and watch state

| | Movies | TV shows | Music | Audiobooks |
| --- | --- | --- | --- | --- |
| Mark watched / finished | ✅ *Watched* | ✅ + season and whole show | ❌ | ✅ *Finished* / *Mark as unread* |
| Start over | ✅ button | ✅ | — | ✅ button (asks first part way through) |
| Shuffle | 🟡 collections only (W) | ✅ | ✅ album, artist, genre | ❌ |
| Radio, mixes | ❌ | ❌ | ✅ | — |
| Next up | — | ✅ moves on after an episode | 🟡 hero resumes | ✅ *Next in series*, offered at the end |
| Remove from Continue | ✅ | ✅ | ❌ | ✅ |
| Speed, sleep timer, skips | player controls | player controls | ❌ sleep timer | ✅ per book |

## Lists

| | Movies | TV shows | Music | Audiobooks |
| --- | --- | --- | --- | --- |
| Watchlist | ✅ | ✅ | ❌ (Follow is the nearest) | ❌ |
| Collections | ✅ W only | ✅ W only | ❌ | 🟡 W only |
| Playlists | ❌ | ❌ | ✅ W; ✅ phones add to one or start one; 🟡 apps can't rename, reorder or delete; TVs only play them | ❌ |
| Favourites | ❌ (only Live TV channels) | ❌ | ❌ | ❌ |

## Looking after a library (main profile, web)

| | Movies | TV shows | Music | Audiobooks |
| --- | --- | --- | --- | --- |
| Fix match | ✅ main profile | 🟡 shows, not episodes | ❌ | ✅ main profile |
| Edit details by hand | ❌ | ❌ | ❌ | ✅ + chapters, author photo and bio |
| Delete | ✅ via Radarr | ✅ via Sonarr, episodes too | ✅ via Lidarr | ✅ files |
| Library health | ✅ duplicates, SD copies, artwork | ✅ | ❌ | 🟡 `.aax` only |
| Pick or upload artwork | ❌ | ❌ | ❌ | ❌ (people only) |
| Metadata without an *arr | NFO, TMDB | NFO, TVmaze | file tags only | Audible, Apple Books, Open Library |
| Kids allow / block a title | ✅ | ✅ | ❌ | — (hidden from kids) |

## Home and the systems around the apps

| | Movies / TV | Music | Audiobooks |
| --- | --- | --- | --- |
| Continue row | ✅ | 🟡 not in Continue on A; *Recently played* instead | ✅ (joins *Continue watching* on A) |
| Apple TV Top Shelf | ✅ | ✅ recently played albums | ❌ |
| Widgets | ✅ Watchlist (i), Continue watching + Watchlist (A) | ❌ | ✅ Continue listening |
| Siri / App Intents (i) | ✅ | ❌ (only play/pause) | ✅ |
| CarPlay / Android Auto | — | ❌ CarPlay; ✅ Android Auto | ✅ both |
| Downloads (phones) | 🟡 single movies and episodes, no whole season | ✅ albums, playlists | ✅ |
| Trakt | ✅ | — | — |
| MCP `search_library` | ✅ | 🟡 no albums, radio or requests | 🟡 in-progress only |

## Search

| | Movies / TV | Music | Audiobooks |
| --- | --- | --- | --- |
| In your library | ✅ every app | ✅ **Artists** and **Albums** in every app (tracks on W only) | ✅ every app |
| Genre results (W) | ✅ open the library's own genre | ✅ its albums | ✅ its genre page |

## Cards and press-and-hold

| | Movies / TV | Music | Audiobooks |
| --- | --- | --- | --- |
| Press-and-hold / right-click menu | ✅ every app | ✅ apps: album, artist, track and playlist cards and tracklist rows (`MusicMenu`); 🟡 W: albums and track rows only; mix cards have none | ✅ |
| Play button on the card (W) | ❌ | ✅ | ✅ |
| Watched tick, progress bar | ✅ | — | ✅ |
| Downloaded mark | ✅ bottom-right (phones) | ✅ bottom-right | ✅ bottom-right |
| Artist shape | — | ✅ round everywhere | — |

## Where one library is ahead

**Music has, the others could take:** a hero led by what you played last; request states as
buttons with undo, greyed when the service is down, *Can't request* reasons, a Requested view with progress;
*Not in your library* on the parent page (missing seasons for a show, missing books in a series); Follow with
new-release notices; the card's play button on the web.

**Movies and TV have, the others could take:** See all on every row; Top 10, genre rows and Coming soon (Lidarr's calendar for music); Fix match and library
health for music; the opens-on setting for music on Android TV; A–Z, search box and scroll restore for books;
Top Shelf, Siri and CarPlay for the kinds missing them.

**Audiobooks have, the others could take:** editing details by hand and locking them; rich filters (progress, length) where movies only filter by genre; *Next in series* as a model for a
film franchise's next film; time left at your speed.

## Same idea, different look or words

Batch 2 brought these into line: book buttons (`BookActions`) and album pages (`MusicActions`), the view switch,
genres (a titled page everywhere), Shuffle's icon, the downloaded mark, artists' shape, grid errors and empty genres.
Still different:

1. A movie request's *Requested* is text; a music one is a button (srv-3).
