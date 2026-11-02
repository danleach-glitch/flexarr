# Fix match and editing details

Two gaps from [library-parity.md](library-parity.md), built together: music had no Fix match and no Library health
checks, and films and shows could only be re-matched, never edited by hand as books can.

## Decisions

- **Music Fix match** works on albums and artists. With Lidarr connected, its albums (or artists) come first; Deezer's
  search (no key) is always offered too, so a library without Lidarr can be fixed. The choice is kept in Flexarr:
  nothing is added to, followed in or changed in Lidarr. *Match automatically again* undoes it.
- **Choosing a cover:** a candidate's covers (Lidarr's, Deezer's) are shown, and the current one; the cover picked is
  kept through refreshes.
- **Editing by hand** for films and shows (title, sort title, original title, year, description, genres, studio or
  network) and albums (title, year, genres, type). The main profile can do it, as it can Fix match. The age rating is
  not editable: kids access stays with *Allow for kids* and *Block for kids*.
- **Fix match to a different title replaces hand edits**: it unlocks the details, and the dialog says so first.
- **Library health for music:** albums without a cover, duplicate albums and split artists. **Merge** makes two
  spellings of an artist, or two copies of an album, one in Flexarr; files are never touched. Albums count as titles in
  the one score.
- Web only. The apps show the results.

## The locks

| Column | Means | Set by | Cleared by |
| --- | --- | --- | --- |
| `MatchLocked` | *which* film, show, album or artist this is: refreshes keep the chosen id | Fix match, Ignore | Match automatically again |
| `DetailsLocked` | the hand-edited text fields stay as they are | Edit details | Back to automatic; Fix match to a different title |
| `ArtworkLocked` | the poster or cover stays as chosen | choosing a cover in Fix match | Match automatically again |

Refreshes of a details-locked title still update everything else: ids, artwork, ratings, cast, seasons and episodes.
Books keep using `MatchLocked` for both, as before.

For music, a match-locked album keeps the Lidarr album (`ArrId`) or Deezer album (`DeezerId`) chosen. A Lidarr album is
re-read on refresh; a Deezer one keeps what it was given when chosen (choose it again to update it). A locked album or artist is never handed
back to its tags. Renames keep the tags' spelling in `OriginalTitle`, which the scanner indexes, so the next scan still
files the album's tracks in it.

## Merging

Merging moves the albums (artist merge) or tracks (album merge) onto the one kept and deletes the other. What the scanner
would have used to find the one merged away (its tag name, its folder) goes into `MusicAliases`, which the scanner reads
with its index, so new or rescanned tracks land in the kept one. Files on disk are untouched.

## API (added beside the old)

- `PUT /items/{id}/details` (films, shows), `PUT /items/{id}/album-details`, `DELETE /items/{id}/details`.
- `GET`, `POST`, `DELETE /items/{id}/music-match` (albums and artists).
- `POST /library-health/merge-artists`, `POST /library-health/merge-albums`.
- New fields with defaults on `ItemDetailDto` (`detailsLocked`, `sortTitle`), `LibraryHealthDto` (music lists) and
  `HealthScoreDto` (music counts).
