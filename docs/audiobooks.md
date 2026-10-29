# Audiobooks: plan

A plan, not built yet. Audiobooks become a library type like films, TV and music: the \*arr app finds and organises the books, Flexarr plays them, and it works without an \*arr too. It's built to stand beside the best dedicated audiobook apps, not to be music with longer tracks.

Decided so far:

- **\*arr:** any Readarr-compatible app (Readarr, Bookshelf, Chaptarr) through one connection type. Readarr itself was retired in June 2025; the forks keep its `/api/v1` and webhooks.
- **Ebooks:** audiobooks first. The data model leaves room for an ebook edition of the same book later.
- **First apps:** web, iPhone and iPad, Android. Apple TV follows.
- **Included:** Audible-quality metadata, requests from inside Flexarr, and an import from Audiobookshelf.

## What people get

Each line lists the phase that delivers it.

### Finding books

- An **Audiobooks** library with shelves: Continue listening, Next in series, Recently added, Series, Authors, Narrators, Genres, and Finished. *(1)*
- Filters for not started, in progress and finished, plus length, narrator, language and abridged. Sorting by title, author, series, date added, release date, length and last listened. *(1)*
- **Book page:**
  - square cover, title and subtitle
  - series link ("Book 3 of 7")
  - authors and narrators, length and time left at the current speed
  - chapter list showing progress
  - description, publisher, year and language
  - Play/Resume, Download, Mark finished, Bookmarks
  - More in the series, More by the author, More read by the narrator *(1)*
- **Series page** in reading order, with the next unread book picked out. Order numbers can be decimals or ranges (1.5, 0, 1–3). *(1)*
- **Author and narrator pages** with photo, biography, books and series, built on the existing person pages. *(1, photos in 3)*
- Home rows ("Continue listening", "Next in series", "New audiobooks") in the Home editor. Search covers books, series, authors and narrators. *(1)*
- Collections, manual and smart, work for books as they do for films. *(1)*

### Listening

- **One timeline per book**, however many files it has. Progress, chapters and the scrubber cover the whole book. *(1)*
- **Chapters:** list, next and previous, and a progress bar for the chapter or the whole book. *(1)*
- **Speed** from 0.5× to 3.5× in 0.05 steps with the pitch kept. Remembered per book, with a default per profile. *(1)*
- **Skip** back and forward by chosen steps (10/15/30/45/60 s). The phone's lock screen, headphones and car use the same steps. *(1 web, 4 iPhone)*
- **Smart rewind:** after a pause, step back a few seconds, more the longer the pause (none under 10 s, about 30 s after an hour). *(1)*
- **Sleep timer:** set minutes or end of chapter, a fade-out, shake to add time (phones), and an optional automatic timer after a set hour. Built on the sleep timer music and podcasts have. *(1 web, 4 iPhone)*
- **Bookmarks** with an optional note, listed on the book page and jumpable from the player. *(1)*
- **Mark finished** when the book ends, or by hand. "Listen again" starts over and keeps the record. *(1)*
- **Across devices:** progress is saved every 15 s and on pause. If another device got further, the player offers to jump there instead of jumping on its own. *(1)*
- **Listening stats** per profile: time per day, streak, books finished per year. These feed *Your year in review* ([#29](https://github.com/danleach-glitch/flexarr/discussions/29)). *(8)*
- **Voice boost and silence trimming**, shared with the podcast idea ([#33](https://github.com/danleach-glitch/flexarr/discussions/33)). *(8)*

### On the phone (iPhone, iPad, Android)

- **Downloads** of a whole book, with progress kept offline and sent when back online (`UnsentProgress`). Optional: download the next book in a series automatically, remove books once finished. A storage view. *(4, 7)*
- **Lock screen and Control Center** controls, with chapter titles and chapter skips. *(4, 7)*
- **CarPlay** (Continue listening, Downloaded, Library) and **Android Auto**. *(4, 7)*
- **Widgets:** Continue listening, with play and pause. *(4)*
- **Siri and Shortcuts:** "Resume my audiobook", "Play *The Hobbit*", "Set a sleep timer for the end of the chapter". *(4)*
- **AirPlay**, and Chromecast on Android. *(4, 7)*

### Getting books

- **Readarr, Bookshelf or Chaptarr:** imports rescan the book straight away. Their metadata fills gaps. Deleting a book in Flexarr deletes it there. Coming soon shows their upcoming releases. *(2)*
- **Requests in Flexarr:** search for a book you don't have and request it. The \*arr is told to find it, and the requester is notified when it's ready. *(5)*
- **Import from Audiobookshelf:** progress, finished books, bookmarks and listening history for each person. *(6)*

## Library layout and scanning

Books arrive in many layouts, and the scanner accepts the common ones:

```
Author/Title/…                                   one book per folder
Author/Series/1 - Title/…                        series and order from the path
Author/Series/Vol. 1.5 - 2004 - Title {Narrator}/
Author/Title/CD1/…, CD2/…                        disc folders are one book
Author/Title.m4b                                 single file directly in an author folder
```

- **Files:** m4b, m4a, mp3, aac, flac, ogg/opus, wma. A folder of audio files is one book, and disc-like subfolders (CD1, Disc 2, Part 3) are parts of it. Files are ordered by disc and track tag, then natural sort of the name.
- **Order of trust, highest first:**
  1. sidecar files: `metadata.opf` (written by Readarr, Bookshelf and Calibre), `desc.txt`, `reader.txt`, `cover.jpg/png`, and Audiobookshelf's `metadata.json`
  2. embedded tags: album for title, album artist or artist for author, composer for narrator, `MVNM`/`MVIN` or grouping for series, `ASIN`/`AUDIBLE_ASIN`, ISBN
  3. the folder path
  4. the \*arr
  5. online providers

  Local always wins, and Flexarr never writes to the files. This is the same rule as non-\*arr libraries.
- **Chapters, first available:**
  1. embedded chapters (`ffprobe -show_chapters`; `MediaProbe` doesn't read them yet)
  2. a `.cue` sheet
  3. one chapter per file, named by its title tag
  4. Audible's chapters by ASIN (phase 3)
  5. evenly spaced "Part n" markers on very long single files with nothing else

  Chapters are stored in the database, so a book keeps them if a provider goes away.
- **Stable identity:** progress belongs to the book, not its files. A re-encode, a merge into one m4b or a rename keeps the book matched by folder, ASIN/ISBN, then title and author. A book whose files vanish is held, not deleted, like other libraries.
- **Not supported:** DRM-protected Audible `.aax`/`.aaxc`. The scanner lists them in library health as "protected, can't be played".

## Data model

The model reuses the `MediaItem` tree so search, collections, visibility, Home rows and downloads keep working:

| Thing | How |
|---|---|
| Library | `LibraryType.Audiobooks`, a new enum value |
| Book | `MediaKind.Audiobook`: a top-level item with its files in order. A new kind, so it's harmless to older apps |
| Edition details | ASIN, ISBN, publisher, release date, language, abridged, explicit, subtitle, and the sum of the files' durations |
| Chapters | new `MediaChapter` table (item, index, title, start, end, source) |
| Authors, narrators | the existing `Person`/`Credit`, with new roles `Author` and `Narrator` (and `Translator`) |
| Series | new `BookSeries` and `BookSeriesEntry` (book, series, sequence as text: "1.5", "0", "1-3"). A book can be in several series (a sub-series and a universe) |
| Progress | `PlaybackState` per profile, using the position on the whole-book timeline, plus a new `FinishedAt` |
| Bookmarks | new `Bookmark` (profile, item, position, note, created) |
| Speed | per book in the existing per-show speed table, with a new `Profile.AudiobookSpeed` default |
| Listening history | new `ListeningSession` (profile, item, device, start, end, seconds listened) for stats and the Audiobookshelf import |
| Ebooks later | an ebook is another file on the same book, and a book can have an audio file, an ebook file, or both |

Things to fix along the way:

- `LibraryVisibility.Hides` sends any unknown kind to Music. Give books their own case.
- The web's `Record<LibraryType, …>` maps (`libraryIcons.ts`, `format.ts`) need the new value.
- Check that the released iPhone and Apple TV apps hide a library whose type decodes as `unknown`, rather than showing it as a music library.

## Playback

- **Manifest:** `GET /items/{id}/book` returns the files with their start offsets on the timeline, the chapters, bookmarks and progress. Clients turn a timeline position into a file and an offset within it.
- **Native apps:** play the files back to back from that manifest: `AVQueuePlayer` on Apple, `ConcatenatingMediaSource` in Media3 on Android. They play each file directly when the device can decode it. Otherwise they use the existing audio conversion to AAC (`PlaybackDecider`).
- **Web:** gets one audio-only HLS playlist for the whole book from the server, built from the existing audio HLS path with each file a run of segments. The browser then sees a single stream it can seek anywhere in.
- **Progress:** `PUT /items/{id}/progress` with the timeline position, the device, and the time the device recorded it. The most recent recording wins. Offline positions are sent later with their original times, so an old offline session can't overwrite newer listening.
- **Finished:** within the last 2 minutes or past 98%, whichever comes first, or when the player reaches the end. Podcasts count as played only at the very end, so books get their own rule.

## \*arr integration (Readarr-compatible)

- **Connection:** `ArrKind.Readarr`, labelled *Readarr / Bookshelf / Chaptarr*, on API v1 like Lidarr. `GET /api/v1/system/status` tells which one it is.
  - **Bookshelf:** one instance per format, so people add their audiobook instance. Flexarr already supports several instances of a kind.
  - **Chaptarr:** handles both formats in one instance. Flexarr keeps only audiobook files, by extension and by Chaptarr's format field.
- **Webhook** `POST /webhooks/readarr?secret=…`:
  - **Rescan** the book's folder (from `bookFiles[].path`) on `Download` (import or upgrade), `Rename`, `Retag`, `BookFileDelete` and `BookDelete`.
  - **Rescan** the author's folder (`author.path`) on `AuthorAdded` and `AuthorDelete`.
  - **Reply** `200` to `Test`; ignore `Grab` and `Health`.
  - **Paths** go through the existing path mappings.
- **Metadata from the \*arr**, with no keys of our own (like `LidarrMatcher`):
  - **Matching:** fetch authors and books once per refresh, then match by book file path, then ASIN/ISBN, then title and author.
  - **Fills:** gaps only — author names, photos and biographies, book descriptions, series and order, covers, release dates.
  - **IDs:** Chaptarr's IDs carry a provider prefix (`hc:`, `gr:`), so they're stored as plain text and never parsed as numbers.
- **Delete:** deleting a book in Flexarr deletes its files through the \*arr and unmonitors the book, so it isn't fetched again.
- **Coming soon and activity:** the calendar (`/api/v1/calendar`) adds upcoming books by followed authors to Coming soon. The queue shows books on their way.
- **Requests** (phase 5): Seerr doesn't handle books, so Flexarr talks to the \*arr directly.
  1. **Search:** `GET /api/v1/book/lookup?term=` shows results with covers and labels for "In your library", "Requested" or "Available to request".
  2. **Request:** adds the author if they're new (with the quality profile, metadata profile and root folder chosen in Settings › Requests). It monitors only that book and starts a search.
  3. **Storage:** requests use the existing `MediaRequest` table, permissions and notifications. Each profile gets a destination library (`RequestAudiobooksLibraryId`), as for shows and films.
  4. **Ready:** the import webhook marks the request available and notifies the requester: "*Project Hail Mary* is ready to listen".

## Metadata providers

Providers sit behind one interface, and admins set their order and region (Audible has separate stores: US, UK, CA, AU, DE, FR, …):

| Provider | Gives | Notes |
|---|---|---|
| Audible, through Audnexus | title, subtitle, authors, narrators, series and order, description, cover, genres, chapters with exact times, author photos and biographies | Lookups by ASIN. Search runs on Audible's catalogue by title and author. Both are unofficial and community-run, so the results are cached and nothing depends on them staying up |
| Open Library | ISBN lookups, covers, author data | Free, no key needed |
| Google Books | descriptions and covers by ISBN or title | Optional key |
| Hardcover | series, ratings, descriptions | Optional token. It's what Bookshelf uses, so IDs line up |

- **Fix match:** search every provider, pick a result, and lock it. This is the existing `MatchLocked` flow.
- **Chapter tools** (phase 8):
  - **Look up:** replace a book's chapters with Audible's.
  - **Shift:** move all of them by a few seconds, since Audible's times are often off by the length of the opening credits.
  - **Edit:** rename chapters by hand.

## Import from Audiobookshelf

Built the way the Plex history import is (`PlexImporter`):

- **Connect** with the server address and an API key (Audiobookshelf 2.26 and later) or a user token. A read-only copy of its SQLite database is the fallback when it's on the same machine and the API can't be reached.
- **People:** each Audiobookshelf user is paired with a Flexarr profile.
- **Brought over:** progress (current time, finished, finished date), bookmarks with titles, and listening sessions for stats.
- **Matching:** by file path through the path mappings, then ASIN/ISBN, then title and author. Newer Flexarr progress is kept. The result reports matched, finished, in progress, bookmarks, unmatched, and kept newer.
- **Metadata edits:** Audiobookshelf's own edits come over via `metadata.json` when it's set to store metadata with the files. The scanner reads that file as a sidecar.

## Apps

- **Web** (phase 1):
  - **Browse:** library and book pages.
  - **Player:** the audio bar grows book controls (chapters, speed, smart rewind, sleep timer, bookmarks). A full-window Now Playing shows the cover and the chapter list.
- **iPhone and iPad** (phase 4):
  - **Screens:** a Books tab, which appears when an audiobook library exists, plus book, series and author screens.
  - **Player:** `MusicPlayer` gains a book mode, much as podcasts did, with `NowPlaying` covering lock screen chapters and skips.
  - **Everything else:** `DownloadStore` support, CarPlay sections, a widget and App Intents. In FlexarrKit, new fields are optional and marked "Absent from servers before …".
- **Android** (phase 7): once the Android app has its audio player, which it doesn't yet: a Media3 session, Android Auto and downloads.
- **Apple TV** (later): Continue listening and a big-screen Now Playing using the music visualiser's look.

## Phases

The work sits behind an **Audiobooks (preview)** setting until phase 4, so main stays releasable.

| # | Phase | Size |
|:-:|---|:-:|
| 0 | **Try things on the Mini:** run Bookshelf and Chaptarr, capture real webhook payloads and API responses as test fixtures, and gather a sample library covering every layout above | S |
| 1 | **Library and web:** library type, scanner, sidecars, tags and chapters, data model and migration, book manifest and HLS, progress, bookmarks, web browse and player | L |
| 2 | **Readarr-compatible connection:** webhook, metadata matcher, delete, Coming soon, Settings › Connections | M |
| 3 | **Online metadata:** Audnexus/Audible, Open Library, Google Books, Hardcover; Fix match; author photos | M |
| 4 | **iPhone and iPad:** browse, player, downloads, lock screen, CarPlay, widget, Siri. Remove the preview setting | L |
| 5 | **Requests** through the \*arr | M |
| 6 | **Import from Audiobookshelf** | S–M |
| 7 | **Android** (after its audio player) | M |
| 8 | **Polish:** listening stats, chapter tools, voice boost and silence trimming, automatic sleep timer, Apple TV | M |
| — | **Later:** ebooks (an EPUB reader, switching between listening and reading) | L |

## Testing

- **Scanner:** test fixtures made with ffmpeg at test time (short tones with chapter metadata, tagged m4b and mp3, disc folders, sidecars). One test per layout above.
- **Webhooks and API:** recorded payloads from Readarr, Bookshelf and Chaptarr from phase 0.
- **Timeline:** position ↔ file mapping, with tests at file boundaries and for files that disagree with their tagged durations.
- **On the Mini:** a real library, with the \*arr connected to it.

## Risks and open questions

- **Chaptarr is beta**, and its API may drift from Readarr's. The phase 0 fixtures and a tolerant parser (unknown fields ignored, IDs kept as text) limit the damage.
- **Audnexus and Audible's catalogue are unofficial.** The cache, local chapters and other providers keep the library working if they change or go away.
- **The Mac app's sandbox:** nothing here runs another program apart from ffprobe/ffmpeg, and library folders are chosen with `FolderPicker`, so it fits.
- **Big libraries** (10,000+ books): browse queries page, and series and author pages are indexed like music's.
- **Still to decide:**
  - whether **"Next in series"** stays a shelf or also auto-plays the next book (iPhone)
  - whether **finished books** should drop out of Continue listening at once or after a day
