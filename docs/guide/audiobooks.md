---
title: Audiobooks
description: An audiobooks library with chapters, series, authors and narrators, a player with speed, smart rewind, a sleep timer and bookmarks, and books on your phone offline.
---

Audiobooks are a library like films, TV and music. Flexarr finds the books in your folders, reads their chapters and details, and plays each book as one, however many files it has. Readarr, Bookshelf or Chaptarr can fetch books for it, but they don't have to: a folder of audiobooks is enough.

Books are on the web, iPhone and iPad, Android and the Apple TV. Kids profiles don't see them.

## Adding the library

In **Settings › Libraries**, add a library, choose **Audiobooks**, and pick the folder your books are in.

Flexarr reads these layouts:

```
Author/Title/…                                   a folder of files is one book
Author/Series/1 - Title/…                        series and number from the folders
Author/Series/Vol. 1.5 - 2004 - Title {Narrator}/
Author/Title/CD1/…, CD2/…                        disc folders are parts of one book
Author/Title.m4b                                 single-file books side by side
Author - Title [B0DPXK6Y7B].m4b                  an Audible id in the name
```

- **Files:** m4b, m4a, mp3, aac, flac, ogg, opus and wma. Books protected by Audible (`.aax`, `.aaxc`) can't be played: **Library health** lists them.
- **Order:** files play by disc, then track number, then name, with numbers in names counted as numbers.
- **Details:** Flexarr reads them from files beside the book first (`metadata.opf` from Readarr, Bookshelf or Calibre, Audiobookshelf's `metadata.json`, `desc.txt`, `reader.txt`, a cover image), then the files' tags, then the folder names. It never changes your files.
- **Chapters** come from the files themselves, a `.cue` sheet, or one per file. A long book in a single file with none is split into half-hour parts.

## Finding books

The library's front page has **Continue listening**, **Next in series**, **Recently added**, your series, authors, narrators and genres, and what you've finished. **All** lists every book, with filters for what you haven't started, are part way through or have finished, and by narrator, length and language.

A book's page shows its series ("Book 3 of 7"), authors and narrators, how long it is and how long is left at your speed, its chapters, and more in the series, by the author and read by the narrator. A series' page lists its books in order with the next one to hear picked out.

**Home** can show **Continue listening**, **Next in series** and **New audiobooks**: turn them on or off in **Settings › Home**.

## Listening

- **Speed** goes from 0.5× to 3.5×. Each book remembers its own; new books start at your default.
- **Skip** back and forward by the steps you choose. Headphones, the lock screen and the car use them too.
- **Smart rewind:** after a pause, a book starts a little earlier, more the longer you were away.
- **Sleep timer:** after some minutes or at the end of the chapter. On a phone, shake it to add time.
- **Bookmarks**, with a note if you like, are on the book's page and in the player.
- **Finished:** a book counts as finished in its last two minutes. **Start over** goes back to the beginning and keeps the record. Part-way through a book, it asks first.
- **Across devices:** your place is saved as you listen. If another device got further, the player offers to jump there.
- At the end of a book in a series, the player offers the next one.

In the player, the seek bar covers the chapter playing; switch it to **Book** to see the whole book. Time left counts at your speed.

Set your default speed, skip steps and smart rewind in **Settings › Audiobooks** on the web, iPhone and Android. A book you've set a speed for in the player keeps its own.

## On your phone

**Download** a book to listen without a connection. Your place is sent when you're back online. In the car, CarPlay and Android Auto list what you're listening to and what you've downloaded.

## Details from the internet

When a book's files leave gaps, Flexarr fills them from Audible (through the community-run Audnexus service), Open Library, and if you add keys, Google Books and Hardcover. Choose your Audible store in the web app's **Settings › Audiobooks**; Google Books and Hardcover turn on once their key or token is saved there.

Audible's chapters replace guessed ones when they match the book's length. To change a book's chapters yourself, open its page, choose **⋯ › Edit chapters**, rename them or move them all by a few seconds, then **Save**. **Use Audible's** fills them in; **Save** keeps them. **Fix match** finds the right book when Flexarr picked the wrong one.

## Readarr, Bookshelf and Chaptarr

Connect any of them in **Settings › Connections** as **Readarr / Bookshelf / Chaptarr**. Bookshelf keeps one kind of book per instance, so connect the one for audiobooks.

With one connected:

- New books show up as soon as they're imported.
- Their details and covers fill what your files don't say.
- **Coming soon** lists books on their way from authors you follow.
- Deleting a book in Flexarr deletes it there too, and it isn't fetched again.
- You can request books from the web app: see [Requests](requests.md).
