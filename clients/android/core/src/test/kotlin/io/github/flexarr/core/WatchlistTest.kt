package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import kotlinx.serialization.builtins.ListSerializer
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.time.LocalDate

class WatchlistTest {
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

    private fun client() = FlexarrClient(server.url("/"), apiKey = "device-token")

    private fun json(body: String, code: Int = 200) =
        MockResponse.Builder().code(code).addHeader("Content-Type", "application/json").body(body).build()

    private val itemJson =
        """{"id":"i1","libraryId":"l","kind":"Movie","title":"Dune","childCount":0,"artwork":{},"userData":{"positionSeconds":0,"watched":false,"playCount":0}}"""

    private fun decodeEntries(text: String) = FlexarrClient.json.decodeFromString(ListSerializer(WatchlistEntry.serializer()), text)

    @Test
    fun `an item from a server before 0_56 has no watchlist`() {
        val detail = FlexarrClient.json.decodeFromString(ItemDetail.serializer(), """{"item":$itemJson}""")
        assertNull(detail.onWatchlist)
    }

    @Test
    fun `an item from a newer server says whether it's on the watchlist`() {
        val detail = FlexarrClient.json.decodeFromString(ItemDetail.serializer(), """{"item":$itemJson,"onWatchlist":true}""")
        assertEquals(true, detail.onWatchlist)
    }

    @Test
    fun `reads the server's entries`() {
        val entries = decodeEntries(
            """[
              {"id":"e1","mediaType":"movie","tmdbId":438631,"title":"Dune","year":2021,"poster":"/api/v1/images/x","backdrop":null,
               "addedAt":"2026-10-30T10:00:00","status":"Ready","releaseDate":"2021-10-22","libraryItemId":"i1","item":$itemJson,"source":"flexarr"},
              {"id":"e2","mediaType":"tv","tmdbId":1399,"title":"Coming Thing","year":2027,"addedAt":"2026-10-30T10:00:00",
               "status":"Coming","releaseDate":"2027-03-05","libraryItemId":null,"item":null,"source":"trakt"},
              {"id":"e3","mediaType":"movie","tmdbId":1,"title":"Asked","addedAt":"2026-10-30T10:00:00","status":"OnItsWay","source":"flexarr"},
              {"id":"e4","mediaType":"movie","tmdbId":2,"title":"Nobody","addedAt":"2026-10-30T10:00:00","status":"NotAvailable","source":"flexarr"}
            ]""",
        )
        assertEquals(listOf(WatchlistStatus.Ready, WatchlistStatus.Coming, WatchlistStatus.OnItsWay, WatchlistStatus.NotAvailable), entries.map { it.status })
        assertEquals("i1", entries[0].item?.id)
        assertTrue(entries[0].isMovie)
        assertEquals(LocalDate.of(2027, 3, 5), entries[1].releaseDay)
        assertEquals("trakt", entries[1].source)
        assertNull(entries[2].releaseDay)
        assertEquals(listOf("e4"), entries.filter(WatchlistFilter.NotAvailable::matches).map { it.id })
        assertEquals(4, entries.filter(WatchlistFilter.Everything::matches).size)
    }

    @Test
    fun `a status from a newer server reads as unknown`() {
        val entry = decodeEntries("""[{"id":"e","mediaType":"movie","title":"X","status":"Archived","somethingNew":1}]""").single()
        assertEquals(WatchlistStatus.Unknown, entry.status)
        assertFalse(WatchlistFilter.Ready.matches(entry))
        assertTrue(WatchlistFilter.Everything.matches(entry))
    }

    @Test
    fun `a server without a watchlist answers none`() = runTest {
        server.enqueue(json("""{"title":"Not Found","status":404}""", code = 404))
        assertNull(client().watchlist())
        // The web app's page for a path it doesn't know.
        server.enqueue(MockResponse.Builder().code(200).addHeader("Content-Type", "text/html").body("<html></html>").build())
        assertNull(client().watchlist())
    }

    @Test
    fun `adds a library title and a TMDB title, and takes one off`() = runTest {
        val entry = """{"id":"e1","mediaType":"movie","tmdbId":2,"title":"Nobody","status":"OnItsWay","source":"flexarr"}"""
        server.enqueue(json("""{"entry":$entry,"requested":true}"""))
        val added = client().addToWatchlist("movie", 2)
        assertTrue(added.requested)
        assertEquals(WatchlistStatus.OnItsWay, added.entry.status)
        val post = server.takeRequest()
        assertEquals("POST", post.method)
        assertEquals("/api/v1/watchlist", post.url.encodedPath)
        assertEquals("""{"mediaType":"movie","tmdbId":2}""", post.body?.utf8())

        server.enqueue(json("""{"entry":$entry,"requested":false}"""))
        client().addToWatchlist("i1")
        assertEquals("""{"libraryItemId":"i1"}""", server.takeRequest().body?.utf8())

        server.enqueue(MockResponse.Builder().code(204).build())
        client().removeLibraryItemFromWatchlist("i1")
        val delete = server.takeRequest()
        assertEquals("DELETE", delete.method)
        assertEquals("i1", delete.url.queryParameter("libraryItemId"))

        // Already gone is fine.
        server.enqueue(json("{}", code = 404))
        client().removeFromWatchlist("e1")
        assertNotNull(server.takeRequest())
    }

    @Test
    fun `a refusal says why`() = runTest {
        server.enqueue(json("""{"title":"Conflict","status":409,"detail":"Titles that aren't in the library need a Seerr server."}""", code = 409))
        val error = runCatching { client().addToWatchlist("tv", 9) }.exceptionOrNull()
        assertTrue(error is FlexarrException.Http)
        assertEquals("Titles that aren't in the library need a Seerr server.", error?.message)
    }
}
