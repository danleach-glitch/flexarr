package io.github.flexarr.books

import io.github.flexarr.core.BookChapter
import io.github.flexarr.core.BookDownloadRules
import io.github.flexarr.core.BookFile
import io.github.flexarr.core.BookManifest
import io.github.flexarr.core.BookProgress
import io.github.flexarr.core.BookSpeed
import io.github.flexarr.core.BookTimeline
import io.github.flexarr.core.ChapterSource
import io.github.flexarr.core.CreditRole
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.ItemDetail
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.Library
import io.github.flexarr.core.LibraryType
import io.github.flexarr.core.LocalBook
import io.github.flexarr.core.LocalPhase
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.PlaybackInfo
import io.github.flexarr.core.SearchResults
import io.github.flexarr.core.SmartRewind
import io.github.flexarr.core.bookNumberLine
import io.github.flexarr.core.bookTimeLine
import io.github.flexarr.core.chapterAt
import io.github.flexarr.core.jumpOffer
import io.github.flexarr.core.nextChapterStart
import io.github.flexarr.core.previousChapterStart
import io.github.flexarr.fixture
import io.github.flexarr.ui.library.inMenu
import io.github.flexarr.ui.library.onTv
import kotlinx.serialization.serializer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Audiobooks on the phone: reading the server's answers with and without the new fields, the timeline, smart rewind. */
class AudiobookTest {
    private inline fun <reified T> decode(text: String): T = FlexarrClient.json.decodeFromString(serializer<T>(), text)

    private val summaryJson = """
        {"id":"b1","libraryId":"l1","kind":"Audiobook","title":"The Hobbit","childCount":0,"durationSeconds":40000,
         "artwork":{"poster":"/api/v1/images/b1/poster"},"userData":{"positionSeconds":1200,"watched":false,"playCount":0},
         "book":{"authors":"J. R. R. Tolkien","authorCount":1,"narrator":"Andy Serkis","series":"Middle-earth","sequence":"1",
                 "durationSeconds":40000,"finishedAt":null}}
    """.trimIndent()

    // Decoding

    @Test
    fun `a book summary reads with its book fields`() {
        val item = decode<ItemSummary>(summaryJson)
        assertEquals(MediaKind.Audiobook, item.kind)
        assertEquals("J. R. R. Tolkien", item.book?.authors)
        assertEquals("1", item.book?.sequence)
        assertFalse(item.book?.finishedAt != null)
    }

    @Test
    fun `older servers' answers read without the new fields`() {
        // Every existing fixture is from before 0.58: none carries book fields, and each still reads.
        val detail = fixture<ItemDetail>("item-movie")
        assertNull(detail.book)
        assertNull(detail.item.book)
        val playback = fixture<PlaybackInfo>("playback")
        assertNull(playback.book)
        val search = fixture<SearchResults>("search")
        assertTrue(search.books.isEmpty())
        assertTrue(search.bookSeries.isEmpty())
    }

    @Test
    fun `a book's page reads its edition, chapters, roles and more`() {
        val detail = decode<ItemDetail>(
            """
            {"item":$summaryJson,"overview":"There and back again.",
             "credits":[{"personId":"p1","name":"J. R. R. Tolkien","role":"Author"},{"personId":"p2","name":"Andy Serkis","role":"Narrator"},
                        {"personId":"p3","name":"Someone","role":"Illustrator"}],
             "book":{"subtitle":"Or There and Back Again","publisher":"HarperCollins","language":"en","abridged":false,"explicit":false,
                     "durationSeconds":40000,"chapterSource":"Embedded","finishedAt":null,"speed":1.25,
                     "series":[{"id":"s1","name":"Middle-earth","sequence":"1","bookCount":4}],
                     "chapters":[{"index":0,"title":"An Unexpected Party","startSeconds":0,"endSeconds":2000}],
                     "bookmarks":[{"id":"bm1","positionSeconds":120.5,"note":"Riddles","createdAt":"2026-10-31T09:12:00Z"}],
                     "moreInSeries":[],"byAuthor":[],"readByNarrator":[],"somethingNewer":true}}
            """.trimIndent(),
        )
        val book = detail.book!!
        assertEquals(ChapterSource.Embedded, book.chapterSource)
        assertEquals(1.25, book.speed!!, 0.0)
        assertEquals("Middle-earth", book.series.single().name)
        assertEquals("Riddles", book.bookmarks.single().note)
        assertEquals(listOf(CreditRole.Author, CreditRole.Narrator, CreditRole.Unknown), detail.credits.map { it.role })
        assertEquals("Book 1 of 4", bookNumberLine(book.series.single().sequence, book.series.single().bookCount))
    }

    @Test
    fun `a playback answer carries the book's manifest`() {
        val info = decode<PlaybackInfo>(
            """
            {"sessionId":"s","method":"DirectPlay","url":"/api/v1/playback/s/book/0","durationSeconds":7200,"startPositionSeconds":3700,
             "item":$summaryJson,
             "book":{"itemId":"b1","durationSeconds":7200,
                     "files":[{"index":0,"fileId":"f0","offsetSeconds":0,"durationSeconds":3600,"container":"mp3","audioCodec":"mp3","size":100,"url":"/api/v1/playback/s/book/0","converted":false},
                              {"index":1,"fileId":"f1","offsetSeconds":3600,"durationSeconds":3600,"audioCodec":"wmav2","size":100,"url":"/api/v1/playback/s/book/1","converted":true}],
                     "chapters":[],"bookmarks":[],
                     "progress":{"positionSeconds":3700,"finished":false,"updatedAt":"2026-10-31T09:00:00Z","device":"Pixel 9"},
                     "speed":1.5,"listening":{"speed":1.5,"skipBackSeconds":15,"skipForwardSeconds":45,"smartRewind":true},
                     "nextInSeries":null}}
            """.trimIndent(),
        )
        val manifest = info.book!!
        assertEquals(2, manifest.files.size)
        assertTrue(manifest.files[1].converted)
        assertEquals(45, manifest.listening.skipForwardSeconds)
        assertEquals("Pixel 9", manifest.progress.device)
    }

    @Test
    fun `search reads books and series`() {
        val results = decode<SearchResults>("""{"titles":[],"books":[$summaryJson],"bookSeries":[{"id":"s1","name":"Middle-earth","bookCount":4}]}""")
        assertEquals("b1", results.books.single().id)
        assertEquals(4, results.bookSeries.single().bookCount)
        assertFalse(results.isEmpty)
    }

    // Library types

    @Test
    fun `an unknown library type is never listed, and audiobooks stay off the TV`() {
        val libraries = decode<List<Library>>(
            """
            [{"id":"1","name":"Films","type":"Movies","itemCount":1},
             {"id":"2","name":"Books","type":"Audiobooks","itemCount":3},
             {"id":"3","name":"Comics","type":"Comics","itemCount":9},
             {"id":"4","name":"Podcasts","type":"Podcasts","itemCount":0}]
            """.trimIndent(),
        )
        assertEquals(listOf(LibraryType.Movies, LibraryType.Audiobooks, LibraryType.Unknown, LibraryType.Podcasts), libraries.map { it.type })
        assertEquals(listOf("1", "2"), libraries.filter { it.inMenu }.map { it.id })
        assertEquals(listOf("1"), libraries.filter { it.onTv }.map { it.id })
    }

    @Test
    fun `the shared libraries fixture still lists every library`() {
        val libraries = fixture<List<Library>>("libraries")
        assertTrue(libraries.filter { it.type != LibraryType.Podcasts }.all { it.inMenu })
    }

    // The timeline

    private fun file(index: Int, offset: Double, duration: Double) = BookFile(index, "f$index", offset, duration, url = "/f/$index")

    private val files = listOf(file(0, 0.0, 600.0), file(1, 600.0, 900.0), file(2, 1500.0, 300.0))

    @Test
    fun `a timeline position maps to its file at the boundaries`() {
        assertEquals(0 to 0.0, BookTimeline.place(files, 0.0).let { it.index to it.offsetSeconds })
        assertEquals(0 to 599.9, BookTimeline.place(files, 599.9).let { it.index to it.offsetSeconds }.let { it.first to Math.round(it.second * 10) / 10.0 })
        // Exactly at a file's start, it's that file's.
        assertEquals(1 to 0.0, BookTimeline.place(files, 600.0).let { it.index to it.offsetSeconds })
        assertEquals(2 to 0.0, BookTimeline.place(files, 1500.0).let { it.index to it.offsetSeconds })
        // Past the end (a file longer than its tag said): the last file, further in.
        assertEquals(2 to 400.0, BookTimeline.place(files, 1900.0).let { it.index to it.offsetSeconds })
        // Before the start: the beginning.
        assertEquals(0 to 0.0, BookTimeline.place(files, -5.0).let { it.index to it.offsetSeconds })
    }

    @Test
    fun `a file position maps back to the timeline`() {
        assertEquals(650.0, BookTimeline.position(files, 1, 50.0), 0.0)
        assertEquals(1500.0, BookTimeline.position(files, 2, 0.0), 0.0)
        files.indices.forEach { index ->
            val there = BookTimeline.position(files, index, 12.0)
            assertEquals(index, BookTimeline.place(files, there).index)
        }
    }

    @Test
    fun `a book's length is the manifest's, or its last file's end`() {
        assertEquals(1800.0, BookTimeline.duration(BookManifest("b", 0.0, files)), 0.0)
        assertEquals(1790.0, BookTimeline.duration(BookManifest("b", 1790.0, files)), 0.0)
    }

    private val chapters = listOf(BookChapter(0, "One", 0.0, 100.0), BookChapter(1, "Two", 100.0, 250.0), BookChapter(2, "Three", 250.0, 400.0))

    @Test
    fun `chapters are found and stepped through`() {
        assertEquals(0, chapters.chapterAt(0.0))
        assertEquals(1, chapters.chapterAt(99.6)) // half a second's grace
        assertEquals(2, chapters.chapterAt(399.0))
        assertEquals(250.0, chapters.nextChapterStart(120.0)!!, 0.0)
        assertNull(chapters.nextChapterStart(300.0))
        // Well into a chapter, Previous goes to its start; just after it starts, to the one before.
        assertEquals(100.0, chapters.previousChapterStart(150.0), 0.0)
        assertEquals(0.0, chapters.previousChapterStart(101.0), 0.0)
        assertEquals(0.0, chapters.previousChapterStart(1.0), 0.0)
    }

    // Smart rewind

    @Test
    fun `smart rewind grows with the pause and stops at thirty seconds`() {
        assertEquals(0.0, SmartRewind.seconds(0), 0.0)
        assertEquals(0.0, SmartRewind.seconds(9_999), 0.0)
        assertEquals(2.0, SmartRewind.seconds(10_000), 0.0)
        val minute = SmartRewind.seconds(60_000)
        val tenMinutes = SmartRewind.seconds(600_000)
        assertTrue(minute in 5.0..15.0)
        assertTrue(tenMinutes > minute && tenMinutes < 30.0)
        assertEquals(30.0, SmartRewind.seconds(3_600_000), 0.0)
        assertEquals(30.0, SmartRewind.seconds(86_400_000), 0.0)
    }

    // Speed and time left

    @Test
    fun `speeds keep to 0_05 steps between half and three and a half`() {
        assertEquals(1.05, BookSpeed.snapped(1.04), 0.0)
        assertEquals(0.5, BookSpeed.snapped(0.1), 0.0)
        assertEquals(3.5, BookSpeed.snapped(9.0), 0.0)
        assertEquals("1×", BookSpeed.label(1.0))
        assertEquals("1.5×", BookSpeed.label(1.5))
        assertEquals("1.25×", BookSpeed.label(1.25))
    }

    @Test
    fun `time left counts the speed`() {
        assertEquals("1h left", bookTimeLine(10_800.0, 3_600.0, 2.0, finished = false))
        assertEquals("3h", bookTimeLine(10_800.0, 0.0, 1.0, finished = false))
    }

    // Elsewhere

    @Test
    fun `another device's newer place is offered, never this device's or an older one`() {
        val progress = BookProgress(positionSeconds = 5000.0, updatedAt = "2026-10-31T10:00:00Z", device = "Pixel 9")
        val at = java.time.Instant.parse("2026-10-31T09:00:00Z").toEpochMilli()
        assertNotNull(progress.jumpOffer(1000.0, at, "Galaxy A17"))
        assertNull(progress.jumpOffer(1000.0, at, "Pixel 9"))
        assertNull(progress.jumpOffer(4990.0, at, "Galaxy A17"))
        assertNull(progress.jumpOffer(1000.0, at + 2 * 3_600_000, "Galaxy A17"))
    }

    // Downloads

    @Test
    fun `a downloaded book is whole only when every file is here`() {
        val manifest = BookManifest("b1", 1800.0, files)
        val item = decode<ItemSummary>(summaryJson)
        val coming = LocalBook("b1", "p", item, manifest, fileNames = listOf("a", null, "c"), phase = LocalPhase.Downloaded)
        assertFalse(coming.isDownloaded)
        assertTrue(coming.copy(fileNames = listOf("a", "b", "c")).isDownloaded)
        val heard = coming.recorded(400.0, finished = false, now = 5)
        assertEquals(400.0, heard.unsent!!.position, 0.0)
        val done = coming.recorded(1800.0, finished = true, now = 9)
        assertEquals(9L, done.finishedAtMillis)
        // Removed when finished only once nothing's left to send, and not while it plays.
        assertTrue(BookDownloadRules.finishedWith(listOf(done), removeWhenFinished = true).isEmpty())
        val sent = done.copy(unsent = null)
        assertEquals(1, BookDownloadRules.finishedWith(listOf(sent), removeWhenFinished = true).size)
        assertTrue(BookDownloadRules.finishedWith(listOf(sent), removeWhenFinished = false).isEmpty())
        assertTrue(BookDownloadRules.finishedWith(listOf(sent), removeWhenFinished = true) { it == "b1" }.isEmpty())
    }
}
