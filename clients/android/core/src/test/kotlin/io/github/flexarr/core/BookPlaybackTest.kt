package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Playing a book: the sleep timer's end of chapter, smart rewind after a seek, and ending a session in order. */
class BookPlaybackTest {
    private val chapters = listOf(BookChapter(0, "One", 0.0, 100.0), BookChapter(1, "Two", 100.0, 250.0), BookChapter(2, "Three", 250.0, 400.0))

    @Test
    fun `a chapter's end is its own, the next one's start, or the book's`() {
        assertEquals(100.0, chapters.chapterEnd(50.0, 400.0), 0.0)
        assertEquals(400.0, chapters.chapterEnd(300.0, 400.0), 0.0)
        val unended = listOf(BookChapter(0, "One", 0.0, 0.0), BookChapter(1, "Two", 100.0, 0.0))
        assertEquals(100.0, unended.chapterEnd(10.0, 500.0), 0.0)
        assertEquals(500.0, unended.chapterEnd(200.0, 500.0), 0.0)
        assertEquals(500.0, emptyList<BookChapter>().chapterEnd(10.0, 500.0), 0.0)
    }

    @Test
    fun `end of chapter moves with Next chapter instead of stopping at once`() {
        val sleep = ChapterSleep()
        sleep.set(chapters, 90.0, 400.0)
        assertEquals(100.0, sleep.end!!, 0.0)
        // Next chapter lands on the old end: it now waits for the end of chapter two.
        val next = chapters.nextChapterStart(90.0)!!
        sleep.seeked(chapters, next, 400.0)
        assertEquals(250.0, sleep.end!!, 0.0)
        assertTrue(sleep.left(next, 1.0)!! > 0.3)
        // Played on past the end (a late check), it still stops: the end is held while it plays.
        assertTrue(sleep.left(251.0, 2.0)!! <= 0.3)
        // Not set: seeking doesn't set it.
        sleep.clear()
        sleep.seeked(chapters, 300.0, 400.0)
        assertNull(sleep.end)
        assertNull(sleep.left(300.0, 1.0))
    }

    @Test
    fun `time left before end of chapter counts the speed`() {
        val sleep = ChapterSleep()
        sleep.set(chapters, 0.0, 400.0)
        assertEquals(50.0, sleep.left(0.0, 2.0)!!, 0.0)
    }

    @Test
    fun `a seek while paused forgets the pause, so playing starts right there`() {
        val pause = BookPause()
        pause.paused(now = 0)
        pause.seeked()
        assertNull(pause.resumed(now = 600_000))
        // Without a seek, the pause counts for smart rewind, once.
        pause.paused(now = 0)
        assertEquals(600_000L, pause.resumed(now = 600_000))
        assertNull(pause.resumed(now = 700_000))
    }

    @Test
    fun `a session paused half a day has lapsed, even after a seek while paused`() {
        val hour = 3_600_000L
        val clock = BookSessionClock()
        val pause = BookPause()
        assertFalse(clock.lapsed(0))
        clock.active(0)
        pause.paused(0)
        // A chapter tapped while paused: the pause is forgotten for smart rewind, but nothing reached the server.
        pause.seeked()
        assertNull(pause.since)
        assertFalse(clock.lapsed(10 * hour))
        assertTrue(clock.lapsed(12 * hour))
        // Reported again: alive.
        clock.active(12 * hour)
        assertFalse(clock.lapsed(13 * hour))
        clock.clear()
        assertFalse(clock.lapsed(30 * hour))
    }

    @Test
    fun `ending a book reports the end before stopping the session`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().code(204).build())
            server.enqueue(MockResponse.Builder().code(204).build())
            FlexarrClient(server.url("/"), apiKey = "key").endBookSession("s1", "b1", 400.0, ended = true, record = true, device = "Phone")
            val report = server.takeRequest()
            assertEquals("POST", report.method)
            assertEquals("/api/v1/playback/s1/progress", report.url.encodedPath)
            assertTrue(report.body!!.utf8().contains("\"ended\":true"))
            val stop = server.takeRequest()
            assertEquals("DELETE", stop.method)
            assertEquals("/api/v1/playback/s1", stop.url.encodedPath)
        }
    }

    @Test
    fun `a session the server let go has its place recorded on the book`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().code(404).build())
            server.enqueue(MockResponse.Builder().addHeader("Content-Type", "application/json").body("{\"positionSeconds\":120}").build())
            val client = FlexarrClient(server.url("/"), apiKey = "key")
            assertFalse(client.reportBookSession("s1", "b1", 120.0, paused = true, ended = false, device = "Phone"))
            assertEquals("/api/v1/playback/s1/progress", server.takeRequest().url.encodedPath)
            val record = server.takeRequest()
            assertEquals("PUT", record.method)
            assertEquals("/api/v1/items/b1/progress", record.url.encodedPath)
            assertTrue(record.body!!.utf8().contains("\"positionSeconds\":120"))
        }
    }

    @Test
    fun `a download takes in the server's newer bookmarks and speed, but keeps a speed chosen offline`() {
        val item = ItemSummary(id = "b1", libraryId = "books", kind = MediaKind.Audiobook, title = "Book", childCount = 0, durationSeconds = 400.0, artwork = Artwork(), userData = UserData(0.0, false, 0))
        val saved = BookManifest("b1", 400.0, listOf(BookFile(0, "f", 0.0, 400.0, url = "/f")), chapters = chapters, speed = 1.0)
        val local = LocalBook("b1", "p", item, saved)
        val fresh = saved.copy(
            files = emptyList(), chapters = emptyList(), bookmarks = listOf(Bookmark("m", 50.0)), speed = 1.5,
            listening = BookListening(skipBackSeconds = 10),
        )
        val refreshed = local.refreshed(fresh).manifest
        assertEquals(1.5, refreshed.speed, 0.0)
        assertEquals(listOf("m"), refreshed.bookmarks.map { it.id })
        assertEquals(10, refreshed.listening.skipBackSeconds)
        // The copy's files and chapters stay: they go together.
        assertEquals(1, refreshed.files.size)
        assertEquals(3, refreshed.chapters.size)
        // A speed chosen offline wins until it's sent.
        val chosen = local.speedChosen(1.25)
        assertEquals(1.25, chosen.unsentSpeed!!, 0.0)
        assertEquals(1.25, chosen.refreshed(fresh).manifest.speed, 0.0)
    }
}
