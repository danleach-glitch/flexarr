package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import kotlinx.serialization.serializer
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/** Books and podcast episodes in Continue watching: what Home asks for, what comes back, and the profile's switch. */
class ContinueListeningTest {
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
    fun `home asks for the kinds it can play, and nothing when it can play neither`() = runTest {
        val client = FlexarrClient(server.url("/"))
        server.enqueue(json("""{"featured":[],"rows":[]}"""))
        client.home(setOf(ContinueListening.Podcasts, ContinueListening.Books))
        val asked = server.takeRequest().url
        assertEquals("/api/v1/home", asked.encodedPath)
        assertEquals("books,podcasts", asked.queryParameter("listening"))

        server.enqueue(json("""{"featured":[],"rows":[]}"""))
        client.home()
        assertNull(server.takeRequest().url.query)
    }

    @Test
    fun `a continue row of a film, a book and a podcast episode`() {
        val home = FlexarrClient.json.decodeFromString(serializer<Home>(), HOME)
        val row = home.rows.single()
        assertEquals("Continue watching or listening", row.title)
        assertEquals(HomeRowStyle.Wide, row.style)
        val (film, book, episode) = row.items

        assertFalse(film.isListening)
        assertNull(film.listeningSubtitle)

        assertTrue(book.isListening)
        assertEquals(MediaKind.Audiobook, book.kind)
        assertEquals("Lee Child, Andrew Child", book.listeningSubtitle)
        assertEquals(0.25, book.progress!!, 0.0001)
        assertEquals("/api/v1/images/b/poster", book.artwork.poster)

        assertTrue(episode.isListening)
        assertEquals(MediaKind.PodcastEpisode, episode.kind)
        assertEquals("The Rest Is History", episode.listeningSubtitle)
        assertEquals(0.5, episode.progress!!, 0.0001)
        val asEpisode = episode.asPodcastEpisode()
        assertEquals("p1", asEpisode.podcastId)
        assertEquals("The Rest Is History", asEpisode.podcastTitle)
        assertEquals(0.5, asEpisode.progress!!, 0.0001)
    }

    @Test
    fun `the switch is read, and saved only when the server sent it`() = runTest {
        val client = FlexarrClient(server.url("/"))
        server.enqueue(json("""{"autoPlayNext":true,"skipIntros":false,"skipRecaps":false,"skipCredits":false,"listeningInContinue":true}"""))
        val preferences = client.playbackPreferences()
        server.takeRequest()
        assertEquals(true, preferences.listeningInContinue)

        server.enqueue(MockResponse.Builder().code(204).build())
        client.setPlaybackPreferences(preferences.copy(listeningInContinue = false).forSaving())
        assertEquals(
            """{"autoPlayNext":true,"skipIntros":false,"skipRecaps":false,"skipCredits":false,"listeningInContinue":false}""",
            server.takeRequest().body?.utf8(),
        )

        // An older server says nothing of it, and isn't told.
        val older = FlexarrClient.json.decodeFromString(
            serializer<PlaybackPreferences>(),
            """{"autoPlayNext":true,"skipIntros":false,"skipRecaps":false,"skipCredits":false}""",
        )
        assertNull(older.listeningInContinue)
        assertFalse(FlexarrClient.json.encodeToString(serializer<PlaybackPreferences>(), older.forSaving()).contains("listeningInContinue"))
    }

    private companion object {
        val HOME = """
            {"featured":[],"rows":[{"id":"continue","title":"Continue watching or listening","style":"wide","items":[
              {"id":"m1","libraryId":"l1","kind":"Movie","title":"Heat","childCount":0,"durationSeconds":6000,
               "artwork":{"backdrop":"/api/v1/images/m1/backdrop"},"userData":{"positionSeconds":600,"watched":false,"playCount":0}},
              {"id":"b","libraryId":"l2","kind":"Audiobook","title":"The Secret","childCount":0,"durationSeconds":40000,
               "artwork":{"poster":"/api/v1/images/b/poster"},"userData":{"positionSeconds":10000,"watched":false,"playCount":0},
               "book":{"authors":"Lee Child, Andrew Child","authorCount":2,"durationSeconds":40000}},
              {"id":"e","libraryId":"l3","parentId":"p1","kind":"PodcastEpisode","title":"Napoleon, part 1","childCount":0,
               "durationSeconds":3600,"artwork":{"poster":"/api/v1/images/p1/poster"},"seriesTitle":"The Rest Is History","seriesId":"p1",
               "userData":{"positionSeconds":1800,"watched":false,"playCount":0},"someNewField":1}
            ]}]}
        """.trimIndent()
    }
}
