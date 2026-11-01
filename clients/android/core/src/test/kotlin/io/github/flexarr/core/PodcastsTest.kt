package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class PodcastsTest {
    private lateinit var server: MockWebServer

    @Before
    fun start() {
        server = MockWebServer()
        server.start()
    }

    @After
    fun stop() {
        server.close()
    }

    private fun json(body: String) = MockResponse.Builder().addHeader("Content-Type", "application/json").body(body).build()

    @Test
    fun `reads chapters, and an older server has none`() = runTest {
        val client = FlexarrClient(server.url("/"))
        server.enqueue(json("""[{"startSeconds":0,"title":"Intro"},{"startSeconds":230,"title":"A gift"}]"""))

        val chapters = client.podcastChapters("e1")
        assertEquals(listOf(PodcastChapter(0.0, "Intro"), PodcastChapter(230.0, "A gift")), chapters)
        assertEquals("/api/v1/podcasts/episodes/e1/chapters", server.takeRequest().target)
        assertEquals(0, chapters.indexAt(100.0))
        assertEquals(1, chapters.indexAt(229.8))

        server.enqueue(MockResponse.Builder().code(404).build())
        assertTrue(client.podcastChapters("e1").isEmpty())
    }

    @Test
    fun `no chapter plays before the first starts`() {
        assertNull(listOf(PodcastChapter(60.0, "Late start")).indexAt(10.0))
        assertNull(emptyList<PodcastChapter>().indexAt(10.0))
    }

    @Test
    fun `times in the notes jump there and addresses open`() {
        val notes = "00:00 Intro\n12:34 The gift (see https://example.com/gift).\n1:02:55 Goodbye\nCall 0800 123:4567 or 99:99"
        val links = noteLinks(notes, durationSeconds = 3_600.0 * 2)
        assertEquals(
            listOf(NoteLink.Seek::class, NoteLink.Seek::class, NoteLink.Web::class, NoteLink.Seek::class),
            links.map { it::class },
        )
        assertEquals(listOf(0.0, 754.0, 3_775.0), links.filterIsInstance<NoteLink.Seek>().map { it.seconds })
        val web = links.filterIsInstance<NoteLink.Web>().single()
        assertEquals("https://example.com/gift", web.url)
        assertEquals(web.url, notes.substring(web.range))
        assertEquals("12:34", notes.substring(links[1].range))
    }

    @Test
    fun `a time past the end of the episode isn't a link`() {
        assertEquals(listOf(30.0), noteLinks("0:30 and 52:50", durationSeconds = 600.0).filterIsInstance<NoteLink.Seek>().map { it.seconds })
    }

    @Test
    fun `podcast preferences carry the listening shared with books, and an older server skips 15 and 30 without smart rewind`() = runTest {
        server.enqueue(json("""{"autoDownload":true,"keepEpisodes":3,"speed":1.3,"showInSidebar":true,"skipBackSeconds":10,"skipForwardSeconds":45,"smartRewind":true}"""))
        server.enqueue(json("""{"autoDownload":true,"keepEpisodes":3,"speed":1.0}"""))
        val client = FlexarrClient(server.url("/"))

        val shared = client.podcastPreferences()
        assertEquals(BookListening(1.3, 10, 45, true), shared.listening)
        assertEquals(Triple(10, 45, true), Triple(shared.podcastSkipBack, shared.podcastSkipForward, shared.podcastSmartRewind))
        assertTrue(1.3 in BookSpeed.choices(shared.speed))

        val older = client.podcastPreferences()
        assertNull(older.listening)
        assertEquals(Triple(15, 30, false), Triple(older.podcastSkipBack, older.podcastSkipForward, older.podcastSmartRewind))
        assertEquals(BookSpeed.presets, BookSpeed.choices(1.0))
    }
}
