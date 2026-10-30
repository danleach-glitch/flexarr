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

    private fun entry(id: String, status: String, added: String?, mediaType: String = "movie", title: String = id, release: String? = null, year: Int? = null) =
        WatchlistEntry(
            id = id, mediaType = mediaType, title = title, addedAt = added, releaseDate = release, year = year,
            status = WatchlistStatus.entries.first { it.raw == status },
        )

    @Test
    fun `reads what's on the list cheaply`() = runTest {
        server.enqueue(json("""{"libraryItemIds":["i1","i2"],"titles":["movie:123","tv:456"]}"""))
        val keys = client().watchlistKeys()
        assertEquals(listOf("i1", "i2"), keys?.libraryItemIds)
        assertEquals(listOf("movie:123", "tv:456"), keys?.titles)
        assertEquals("/api/v1/watchlist/keys", server.takeRequest().url.encodedPath)
    }

    @Test
    fun `a server without a watchlist has no keys`() = runTest {
        server.enqueue(json("""{"title":"Not Found","status":404}""", code = 404))
        assertNull(client().watchlistKeys())
        server.enqueue(MockResponse.Builder().code(200).addHeader("Content-Type", "text/html").body("<!doctype html><html></html>").build())
        assertNull(client().watchlistKeys())
    }

    @Test
    fun `ready first, then on its way, coming and not available, newest added first within each`() {
        val entries = listOf(
            entry("na", "NotAvailable", "2026-10-30T12:00:00"),
            entry("coming", "Coming", "2026-10-30T11:00:00"),
            entry("readyOld", "Ready", "2026-10-01T10:00:00"),
            entry("way", "OnItsWay", "2026-10-29T10:00:00"),
            entry("readyNew", "Ready", "2026-10-30T10:00:00"),
            entry("readyUndated", "Ready", null),
        )
        assertEquals(listOf("readyNew", "readyOld", "readyUndated", "way", "coming", "na"), WatchlistSort.ReadyFirst.sorted(entries).map { it.id })
        assertEquals(WatchlistSort.ReadyFirst, WatchlistSort.entries.first())
        assertEquals(listOf("na", "coming", "readyNew", "way", "readyOld", "readyUndated"), WatchlistSort.RecentlyAdded.sorted(entries).map { it.id })
    }

    @Test
    fun `sorts by release date and title`() {
        val entries = listOf(
            entry("late", "Coming", null, release = "2027-03-05"),
            entry("none", "NotAvailable", null),
            entry("yearOnly", "Ready", null, year = 2020),
            entry("early", "Ready", null, release = "2019-06-01"),
        )
        assertEquals(listOf("early", "yearOnly", "late", "none"), WatchlistSort.ReleaseDate.sorted(entries).map { it.id })
        val titled = listOf(entry("b", "Ready", null, title = "‘Blade Runner’"), entry("a", "Ready", null, title = "alien"), entry("c", "Ready", null, title = "Casablanca"))
        assertEquals(listOf("a", "b", "c"), WatchlistSort.Title.sorted(titled).map { it.id })
    }

    @Test
    fun `filters to films or shows`() {
        val entries = listOf(entry("m", "Ready", null, mediaType = "movie"), entry("t", "Coming", null, mediaType = "tv"))
        assertEquals(listOf("m", "t"), entries.filter(WatchlistKind.All::matches).map { it.id })
        assertEquals(listOf("m"), entries.filter(WatchlistKind.Films::matches).map { it.id })
        assertEquals(listOf("t"), entries.filter(WatchlistKind.Shows::matches).map { it.id })
    }

    @Test
    fun `says where a title streams, when the server knows`() {
        val entries = decodeEntries(
            """[
              {"id":"a","mediaType":"movie","title":"A","status":"NotAvailable","streaming":["Netflix"]},
              {"id":"b","mediaType":"tv","title":"B","status":"Coming","streaming":["Netflix","Prime Video","Disney Plus"]},
              {"id":"c","mediaType":"movie","title":"C","status":"NotAvailable"},
              {"id":"d","mediaType":"movie","title":"D","status":"NotAvailable","streaming":null},
              {"id":"e","mediaType":"movie","title":"E","status":"NotAvailable","streaming":[]}
            ]""",
        )
        assertEquals(listOf("Netflix"), entries[0].streaming)
        assertEquals("On Netflix", entries[0].streamingLine)
        assertEquals("Netflix, Prime Video, Disney Plus", entries[1].streamingLine)
        assertNull(entries[2].streaming)
        assertNull(entries[2].streamingLine)
        assertNull(entries[3].streamingLine)
        assertNull(entries[4].streamingLine)
    }

    @Test
    fun `the widget asks for what's ready, without Seerr`() = runTest {
        server.enqueue(json("[$itemJson]"))
        val ready = client().readyFromWatchlist(4)
        assertEquals(listOf("i1"), ready.map { it.id })
        val request = server.takeRequest()
        assertEquals("/api/v1/watchlist/ready", request.url.encodedPath)
        assertEquals("4", request.url.queryParameter("take"))
    }

    @Test
    fun `a server without the ready list gives the ready titles from the whole watchlist`() = runTest {
        val list = """[
          {"id":"e1","mediaType":"movie","title":"Dune","status":"Ready","item":$itemJson},
          {"id":"e2","mediaType":"tv","title":"Coming Thing","status":"Coming"}
        ]"""
        // The web app's page for a path it doesn't know, then the whole list.
        server.enqueue(MockResponse.Builder().code(200).addHeader("Content-Type", "text/html").body("<html></html>").build())
        server.enqueue(json(list))
        assertEquals(listOf("i1"), client().readyFromWatchlist(4).map { it.id })
        assertEquals("/api/v1/watchlist/ready", server.takeRequest().url.encodedPath)
        assertEquals("/api/v1/watchlist", server.takeRequest().url.encodedPath)

        // A 404 reads the same; a server with no watchlist at all has nothing ready.
        server.enqueue(json("""{"status":404}""", code = 404))
        server.enqueue(json("""{"status":404}""", code = 404))
        assertTrue(client().readyFromWatchlist(4).isEmpty())
    }

    @Test
    fun `reads titles from beyond the library`() = runTest {
        server.enqueue(json(
            """[
              {"tmdbId":157336,"mediaType":"movie","title":"Interstellar","year":2014,"overview":"Space.","poster":"/api/v1/images/remote?u=x",
               "backdrop":null,"rating":8.4,"availability":"None","libraryItemId":null,"onWatchlist":true},
              {"tmdbId":1399,"mediaType":"tv","title":"Game of Thrones","year":2011,"rating":8.5,"availability":"Available","libraryItemId":"i1"},
              {"tmdbId":5,"mediaType":"tv","title":"New","rating":0,"availability":"SomethingNew","extra":1}
            ]""",
        ))
        val titles = client().searchEverything("inter stellar")
        assertEquals("inter stellar", server.takeRequest().url.queryParameter("q"))
        assertEquals(true, titles[0].onWatchlist)
        assertEquals("movie:157336", titles[0].key)
        assertEquals("Film, 2014", titles[0].kindLine)
        assertTrue(titles[0].canRequest)
        // A server before watchlists doesn't say.
        assertNull(titles[1].onWatchlist)
        assertEquals("i1", titles[1].libraryItemId)
        assertEquals("Show, 2011", titles[1].kindLine)
        assertFalse(titles[1].canRequest)
        assertEquals(TitleAvailability.Unknown, titles[2].availability)
        assertEquals("Show", titles[2].kindLine)
        assertFalse(titles[2].canRequest)
    }

    @Test
    fun `says whether requests are set up`() = runTest {
        server.enqueue(json("""{"configured":true,"reachable":false}"""))
        assertEquals(RequestsStatus(configured = true, reachable = false), client().requestsStatus())
        server.enqueue(json("""{"status":404}""", code = 404))
        assertNull(client().requestsStatus())
    }

    @Test
    fun `takes a title from beyond the library off by its TMDB id`() = runTest {
        server.enqueue(MockResponse.Builder().code(204).build())
        client().removeFromWatchlist("tv", 1399)
        val delete = server.takeRequest()
        assertEquals("DELETE", delete.method)
        assertEquals("tv", delete.url.queryParameter("mediaType"))
        assertEquals("1399", delete.url.queryParameter("tmdbId"))
    }

    @Test
    fun `an entry the library hasn't got says what it is`() {
        assertEquals("Film, 2014", entry("a", "NotAvailable", null, year = 2014).kindLine)
        assertEquals("Show, 2011", entry("b", "Coming", null, mediaType = "tv", year = 2011).kindLine)
    }
}
