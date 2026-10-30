package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import kotlinx.serialization.serializer
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

/** FlexarrKit's DiscoverTests, against the same fixtures, and the requests the calls make. */
class DiscoverTest {
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

    private inline fun <reified T> fixture(name: String): T = FlexarrClient.json.decodeFromString(serializer<T>(), text(name))

    private fun text(name: String) = javaClass.classLoader!!.getResource("$name.json")!!.readText()

    private fun json(body: String) = MockResponse.Builder().addHeader("Content-Type", "application/json").body(body).build()

    @Test
    fun `discover's rows mark what the library has`() {
        val rows = fixture<List<ExternalRow>>("requests-discover")
        assertEquals(listOf("trending", "movies", "tv", "upcoming"), rows.map { it.id })
        val first = rows.first().items.first()
        assertEquals("movie", first.mediaType)
        assertFalse(first.isShow)
        assertTrue(first.poster!!.startsWith("/api/v1/images/remote?"))
        assertEquals("movie-${first.tmdbId}", first.id)
        // One the library has opens as itself.
        val owned = rows.flatMap { it.items }.first { it.libraryItemId != null }
        assertEquals(Availability.Available, owned.availability)
        assertTrue(rows.flatMap { it.items }.any { it.availability == Availability.Requested })
    }

    @Test
    fun `a title from outside the library has its ratings, cast and more like it`() {
        val title = fixture<ExternalDetail>("requests-title")
        assertEquals("The Dog Stars", title.title)
        assertEquals(119, title.runtimeMinutes)
        assertNull(title.seasons)
        assertEquals("R", title.certification)
        assertEquals(41, title.criticScore)
        assertEquals(6.4, title.imdbRating!!, 0.0)
        assertEquals(listOf("Ridley Scott"), title.madeBy)
        assertEquals("Hig", title.cast.first().character)
        assertEquals(Availability.None, title.availability)
        assertNull(title.libraryItemId)
        assertTrue(title.recommendations.isNotEmpty())
        assertEquals("movie-1384216", title.summary.id)
    }

    @Test
    fun `the library's genres have counts and a backdrop`() {
        val genres = fixture<List<Genre>>("genres")
        assertEquals("Drama", genres.first().name)
        assertTrue(genres.first().movies > 0)
        assertTrue(genres.first().backdrop!!.startsWith("/api/v1/images/items/"))
    }

    @Test
    fun `a newer server's availability reads as unknown`() {
        val title = FlexarrClient.json.decodeFromString(
            serializer<ExternalTitle>(),
            """{"tmdbId":1,"mediaType":"tv","title":"New","rating":7,"availability":"Blocked"}""",
        )
        assertEquals(Availability.Unknown, title.availability)
        assertTrue(title.isShow)
    }

    @Test
    fun `a server before requests can't take them`() = runTest {
        val client = FlexarrClient(server.url("/"))
        server.enqueue(json("""{"configured":true,"reachable":false}"""))
        server.enqueue(MockResponse.Builder().code(404).build())

        val status = client.requestStatus()
        assertTrue(status.configured)
        assertFalse(status.reachable)
        assertEquals(RequestStatus(configured = false, reachable = false), client.requestStatus())
    }

    @Test
    fun `searches, opens and requests a title`() = runTest {
        val client = FlexarrClient(server.url("/"))
        server.enqueue(json("""[{"tmdbId":96677,"mediaType":"tv","title":"Lupin","rating":7.7,"availability":"None","libraryItemId":null}]"""))
        server.enqueue(json(text("requests-title")))
        server.enqueue(MockResponse.Builder().code(204).build())

        val found = client.searchEverything("lupin")
        assertEquals("/api/v1/requests/search?q=lupin", server.takeRequest().target)
        val title = found.single()
        assertEquals("tv-96677", title.id)

        assertNotNull(client.externalTitle(title))
        assertEquals("/api/v1/requests/title/tv/96677", server.takeRequest().target)

        client.request(title)
        val sent = server.takeRequest()
        assertEquals("POST", sent.method)
        assertEquals("/api/v1/requests", sent.target)
        assertEquals("""{"mediaType":"tv","tmdbId":96677}""", sent.body?.utf8())
    }

    @Test
    fun `a genre's titles, narrowed to films`() = runTest {
        val client = FlexarrClient(server.url("/"))
        server.enqueue(json("""{"total":0,"items":[]}"""))
        server.enqueue(json(text("genres")))

        assertEquals(0, client.genreItems("Action & Adventure", MediaKind.Movie).total)
        assertEquals("/api/v1/genres/Action%20&%20Adventure/items?take=120&kind=Movie", server.takeRequest().target)
        assertEquals(3, client.genres().size)
        assertEquals("/api/v1/genres", server.takeRequest().target)
    }

    @Test
    fun `a genre's name stays one segment of the path`() = runTest {
        val client = FlexarrClient(server.url("/"))
        repeat(3) { server.enqueue(json("""{"total":0,"items":[]}""")) }

        client.genreItems("Action/Adventure")
        assertEquals("/api/v1/genres/Action%2FAdventure/items?take=120", server.takeRequest().target)
        // A backslash is a separator to OkHttp: encoded, it can't climb to another of the server's paths.
        client.genreItems("x\\..\\..\\me")
        assertEquals("/api/v1/genres/x%5C..%5C..%5Cme/items?take=120", server.takeRequest().target)
        client.genreItems("100% Drama")
        assertEquals("/api/v1/genres/100%25%20Drama/items?take=120", server.takeRequest().target)
    }
}
