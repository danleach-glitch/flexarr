# Music libraries like Movies and TV: plan

A music library's page works the way a film or TV library's does on every client: a full-bleed hero at the top, the
rows under it, and a switch for **Browse · Artists · Albums · Genres**.

## What people get

### Browse

- **The hero.** The first slide is the album the profile played last, over its artist's fanart, with **Resume**,
  which carries on from the next track (or inside a track left half way), and **More info**, which opens the album.
  After it come up to three artists the profile plays (only ones with fanart), each with **Play** (the artist
  shuffled) and **Radio**, and up to two of the newest albums with Play. A profile that hasn't played anything gets
  artists with fanart and new albums. Six slides at most. Artist logos stand in for the title where Lidarr has one.
- **The rows** stay as they are, in the profile's order (Settings › Home & browse › Music), and the page ends with
  **Your artists**, whose *See all* opens Artists.

### Artists

Every artist as a round picture, A–Z, with the A–Z rail. It used to sit under the rows on Browse.

### Albums

Every album in the library as a square cover with its artist underneath, sortable **A–Z**, **Recently added** or
**Year**, with the A–Z rail when sorted by name.

### Genres

A tile for each genre, biggest first, with an artist's fanart behind it. A genre counts its albums, whether the genre
is tagged on the album or on its artist (Lidarr tags some genres only on albums). A tile opens the genre's albums
under **Play** and **Shuffle**, which play up to 200 of its tracks.

### Elsewhere

- Apple TV: Settings › Libraries › **Opens on** has a choice for music: Browse, Artists, Albums or Genres.
- Apple TV: the Top Shelf gains a **Recently played** section of album covers. Select opens one; Play plays it. The
  Top Shelf belongs to the whole app, so it sits beside Continue watching.

## Server

All additions: older apps never read them.

| What | Where |
| --- | --- |
| `featured`, `overviews` (artist bios) and `resumes` (album id → the track to start from) on a music library's `GET libraries/{id}/browse` | `DiscoveryEndpoints`, `MusicDiscovery.HeroAsync` |
| `GET libraries/{id}/albums?sort=title\|added\|year&genre&skip&take`, paged like `items` | `ItemEndpoints` |
| `GET libraries/{id}/album-genres`: name, album count, backdrop | `ItemEndpoints` |
| `GET libraries/{id}/tracks?genre&shuffle&take` (at most 200) | `ItemEndpoints` |

`libraries/{id}/genres` keeps counting artists for music, so what it means doesn't change.

## Clients

| | Web | iPhone | Apple TV | Android | Android TV |
| --- | --- | --- | --- | --- | --- |
| Hero | ✅ | ✅ smaller | ✅ | ✅ smaller | ✅ |
| Browse · Artists · Albums · Genres | ✅ (Playlists stays a link) | ✅ | ✅ | ✅ | ✅ |
| Genre Play and Shuffle | ✅ | ✅ | ✅ | ✅ | ✅ |
| Opens on | | | ✅ | | |
| Top Shelf | | | ✅ | | |

With an older server the apps keep today's page: no hero (there's no `featured`), and only Browse and Artists, since
Albums and Genres need the new endpoints (a 404 means "not offered").

## Words

**Artists**, **Albums**, **Genres**, **Your artists**, **Radio**, **Resume**, **Play**, **Shuffle**.
