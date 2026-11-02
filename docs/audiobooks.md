# Audiobooks: plan

Being built on `library/audiobooks`. Audiobooks become a library type like films, TV and music: books go in a folder, by hand or with any app, and Flexarr plays them. It's built to stand beside the best dedicated audiobook apps, not to be music with longer tracks.

Decided so far:

- **No \*arr connection (dropped 1 November 2026, before release).** The plan was to connect any Readarr-compatible app (Readarr, Bookshelf, Chaptarr) for imports, metadata, Coming soon, deleting and requests. It was built, then taken out: Bookshelf's metadata server can't search by title (it falls back to Goodreads, which times out), doesn't know Audible ASINs, and rate-limits, so on the household server it matched 1 book in 43 and requests, Coming soon and delete-through had nothing to work with. Online metadata (Audible, Apple Books, Open Library, Google Books, Hardcover) and the files themselves do the job instead. The sections below that described the connection are gone; phases 0, 2 and 5 were dropped.
- **Ebooks:** audiobooks first. The data model leaves room for an ebook edition of the same book later.
- **First release:** phases 1–5 below plus Android: library and web, online metadata, the iPhone and iPad app, and the Android app. Chapter tools, Apple TV listening, and Home Assistant and MCP come with it. The branch lands whole, so there's no preview setting.
- **Later:** the Audiobookshelf import, listening stats, voice boost and silence trimming, the automatic sleep timer, Android TV, ebooks.
- **Kids profiles** never see audiobooks, like podcasts.
- **The end of a book in a series** offers the next book with a Play button; it never starts by itself. A finished book leaves Continue listening straight away.
- **Siri:** the iPhone is at Apple's limit of 10 phrases, so the podcast resume phrase becomes one *Resume listening* for books and episodes. The other book actions live in the Shortcuts app.
- **Widget:** the existing *Continue listening* widget shows whatever played last, book or podcast.
- **Every app skips library types it doesn't know.** Released apps before this show an unknown library as a film grid; that's accepted while few people use them.

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

- **Import from Audiobookshelf:** progress, finished books, bookmarks and listening history for each person. *(6)*

## Library layout and scanning

Books arrive in many layouts, and the scanner accepts the common ones:

```
Author/Title/…                                   one book per folder
Author/Series/1 - Title/…                        series and order from the path
Author/Series/Vol. 1.5 - 2004 - Title {Narrator}/
Author/Title/CD1/…, CD2/…                        disc folders are one book
Author/Title.m4b                                 single file directly in an author folder
Author/Series/Title/Author - TMC1 Title.m4b      series folder without numbers, order in the file name
Author/Book 1/Book 2/…                           a book folder inside another book's folder
Author A, Author B/Title/…                       several authors in one folder name
Author - Title [B0DPXK6Y7B].m4b                  an Audible ASIN in the file name
```

- **Files:** m4b, m4a, mp3, aac, flac, ogg/opus, wma. A folder of audio files is one book, and disc-like subfolders (CD1, Disc 2, Part 3) are parts of it. Files are ordered by disc and track tag, then natural sort of the name.
- **Order of trust, highest first:**
  1. sidecar files: `metadata.opf` (written by Calibre and other book managers), `desc.txt`, `reader.txt`, `cover.jpg/png`, and Audiobookshelf's `metadata.json`
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
- The released apps don't hide a library whose type decodes as `unknown`: iPhone, Apple TV, Android and Android TV show it as a film grid and open its items as videos. Every app now skips unknown library types.
- `ShowPlaybackSpeed` deletes a 1× row as "use the default". With a profile default other than 1×, books store an explicit 1×.

## Playback

Every client plays a book's files back to back on one timeline. There is no whole-book stream: m4b, mp3 and m4a play as they are everywhere, a single-file m4b (the common case) is one stream anyway, and conversion is needed only for the rare file a player can't decode, which the server converts to AAC once and keeps.

- **Start:** `POST /playback` with the book's id, the player's `profile.audioCodecs` and an optional `deviceName`. The answer is the usual `PlaybackInfoDto` with `book` set: a `BookManifestDto` whose file addresses are the session's (`/playback/{sid}/book/{n}`, no key needed, so AirPlay and Chromecast can fetch them). `startPositionSeconds` is on the whole timeline.
- **Timeline:** file *n* starts at `files[n].offsetSeconds`; a timeline position maps to the last file whose offset is at or before it. Chapters, bookmarks and progress are all on the timeline.
- **Progress:** the session's `POST /playback/{sid}/progress` takes timeline positions. Without a session (offline listening, a download), `PUT /items/{id}/progress` `{positionSeconds, device, recordedAt, ended}`; the newest `recordedAt` wins.
- **Finished:** within the last 2 minutes or past 98%, or when the player reports `ended`. Finishing counts a play and sets `finishedAt`; position goes back to 0.
- **Elsewhere:** the manifest's `progress` carries the device that recorded it and when. A player that finds the server's place newer than its own, from another device, offers to jump there.

## API

All under `/api/v1`, for the current profile. Fields new to existing DTOs are optional for apps ("Absent from servers before 0.58").

| Call | What |
|---|---|
| `GET /libraries/{id}/book-shelves` | `BookShelvesDto`: continueListening, nextInSeries, recentlyAdded, series, authors, narrators, genres, finished |
| `GET /libraries/{id}/books?search&progress=notstarted\|inprogress\|finished&author&narrator&genre&language&abridged&minHours&maxHours&sort=title\|author\|series\|added\|released\|length\|listened&descending&skip&take` | `PagedDto<ItemSummaryDto>` |
| `GET /libraries/{id}/book-series?search&skip&take` | `PagedDto<BookSeriesSummaryDto>` (covers of its first books, the next unfinished one) |
| `GET /libraries/{id}/book-people?role=author\|narrator&search&skip&take` | `PagedDto<BookPersonDto>` |
| `GET /book-series/{id}` | `BookSeriesDto`: books in reading order with their numbers, `nextId` |
| `GET /items/{id}` | `ItemDetailDto` with `book`: `BookDetailDto` (edition, series, chapters, bookmarks, speed, moreInSeries, byAuthor, readByNarrator); credits carry `Author`/`Narrator`/`Translator` roles |
| `GET /people/{id}` | `PersonDto` with `biography` |
| `GET /items/{id}/book?codecs=aac,mp3` | `BookManifestDto` with the item's own file addresses (key needed), for downloads |
| `GET /items/{id}/book/files/{n}[?convert=aac]` | the file, with byte ranges |
| `PUT /items/{id}/progress` | `BookProgressRequest` → `BookProgressDto` |
| `POST /items/{id}/finished` `{finished}` · `POST /items/{id}/listen-again` | mark finished or not; start over keeping `finishedAt` |
| `GET/POST /items/{id}/bookmarks` · `PUT/DELETE /bookmarks/{id}` | `{positionSeconds, note}`, notes up to 2,000 characters |
| `PUT /items/{id}/playback-speed` `{speed}` | a book's own speed, 0.25–4 (apps offer 0.5–3.5 in 0.05 steps) |
| `GET/PUT /books/listening` | `BookListeningDto`: default speed, skip back/forward (10, 15, 30, 45 or 60 s), smart rewind |
| `GET /search?q` | `books` and `bookSeries` alongside titles; books aren't in `titles` |
| `GET /home` | rows `books-continue`, `books-next`, `books-new` (style `poster`, square covers) |
| Smart collections | `rules.type = "book"`; manual collections take books |

`ItemSummaryDto.book` (`BookSummaryDto`): authors (two at most, "A, B"), authorCount, narrator, series, sequence, durationSeconds, finishedAt. `MediaKind.Audiobook`, `LibraryType.Audiobooks`. Smart rewind is the player's: none under 10 s of pause, then growing to about 30 s after an hour.

## Metadata providers

Providers sit behind one interface, and admins set their order and region (Audible has separate stores: US, UK, CA, AU, DE, FR, …):

| Provider | Gives | Notes |
|---|---|---|
| Audible, through Audnexus | title, subtitle, authors, narrators, series and order, description, cover, genres, chapters with exact times, author photos and biographies | Lookups by ASIN. Search runs on Audible's catalogue by title and author. Both are unofficial and community-run, so the results are cached and nothing depends on them staying up |
| Apple Books, through the iTunes Search API | title, series and number (read from the title), authors, description, large covers, publisher, release date, one genre | No key. Search only: it lists no ISBNs, ASINs, narrators or chapters. Apple asks for about 20 requests a minute at most; results are cached like the rest |
| Open Library | ISBN lookups, covers, author data | Free, no key needed |
| Google Books | descriptions and covers by ISBN or title | Optional key |
| Hardcover | series, ratings, descriptions | Optional token |

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
- **Android:** the app already has a Media3 audio service (`AudioService`, `PodcastPlayer`, `CarLibrary` for Android Auto, a sleep timer with shake). Books add a book player on that service, downloads, Android Auto tabs, the shared widget and Cast.
- **Apple TV:** Continue listening, book pages and a big-screen Now Playing using the music visualiser's look.

## Phases

The first release covers phases 0, 1, 3, 4 and 7, plus chapter tools, Apple TV listening and Home Assistant/MCP from phase 8. It lands as one branch, so main never has it half-built.

| # | Phase | Size |
|:-:|---|:-:|
| 0 | **Try things on the Mini:** gather a sample library covering every layout above | S |
| 1 | **Library and web:** library type, scanner, sidecars, tags and chapters, data model and migration, book manifest and HLS, progress, bookmarks, web browse and player | L |
| 2 | ~~\*arr connection~~ (dropped, see the top) | — |
| 3 | **Online metadata:** Audnexus/Audible, Open Library, Google Books, Hardcover; Fix match; author photos | M |
| 4 | **iPhone and iPad:** browse, player, downloads, lock screen, CarPlay, widget, Siri. Remove the preview setting | L |
| 5 | ~~Requests through the \*arr~~ (dropped with it) | — |
| 6 | **Import from Audiobookshelf** | S–M |
| 7 | **Android** (after its audio player) | M |
| 8 | **Polish:** listening stats, chapter tools, voice boost and silence trimming, automatic sleep timer, Apple TV | M |
| — | **Later:** ebooks (an EPUB reader, switching between listening and reading) | L |

## Testing

- **Scanner:** test fixtures made with ffmpeg at test time (short tones with chapter metadata, tagged m4b and mp3, disc folders, sidecars). One test per layout above.
- **Timeline:** position ↔ file mapping, with tests at file boundaries and for files that disagree with their tagged durations.
- **On the Mini:** a real library.

## Risks and open questions

- **Audnexus and Audible's catalogue are unofficial.** The cache, local chapters and other providers keep the library working if they change or go away.
- **The Mac app's sandbox:** nothing here runs another program apart from ffprobe/ffmpeg, and library folders are chosen with `FolderPicker`, so it fits.
- **Big libraries** (10,000+ books): browse queries page, and series and author pages are indexed like music's.
