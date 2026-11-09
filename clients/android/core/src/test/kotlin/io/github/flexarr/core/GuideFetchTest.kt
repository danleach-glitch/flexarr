package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.time.Instant

class GuideFetchTest {
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

    private fun channel(id: String, group: String? = null, favourite: Boolean = false) = LiveChannel(id, 1.0, id, group = group, isFavourite = favourite)

    private val guide = """{"from":"2026-11-07T10:00:00Z","until":"2026-11-07T16:00:00Z","channels":[{"id":"a","number":1,"name":"A","programmes":[]}]}"""

    @Test
    fun `the guide is asked only for the channels shown`() {
        val all = (1..300).map { channel("c$it", group = if (it <= 150) "UK| NEWS" else "Sport", favourite = it <= 120) }
        // Every channel: nothing narrower.
        assertEquals(GuideFilter.Everything, GuideFilter.forShown(all, all, favouritesOnly = false))
        // A few: by id.
        assertEquals(mapOf("channels" to "c1,c2,c3"), GuideFilter.forShown(all.take(3), all, favouritesOnly = false).query)
        // Too many for their ids: favourites, or the one group they make up whole, as the source names it.
        assertEquals(mapOf("favourites" to "true"), GuideFilter.forShown(all.take(120), all, favouritesOnly = true).query)
        assertEquals(mapOf("group" to "UK| NEWS"), GuideFilter.forShown(all.take(150), all, favouritesOnly = false).query)
        // Part of a group, or two groups (shown by one tidied name, say): every channel, picked out on the phone.
        assertEquals(GuideFilter.Everything, GuideFilter.forShown(all.take(140), all, favouritesOnly = false))
        assertEquals(GuideFilter.Everything, GuideFilter.forShown(all.take(200), all, favouritesOnly = false))
        assertEquals(GuideFilter.Everything, GuideFilter.forShown(emptyList(), all, favouritesOnly = true))
    }

    @Test
    fun `the guide over the picture asks for its channels by id when there are few enough`() {
        val all = (1..150).map { channel("c$it") }
        assertEquals(mapOf("channels" to "c1,c2"), GuideFilter.byIds(all.take(2)).query)
        assertEquals("c100", GuideFilter.byIds(all.take(100)).query["channels"]!!.split(",").last())
        assertEquals(GuideFilter.Everything, GuideFilter.byIds(all.take(101)))
        assertEquals(GuideFilter.Everything, GuideFilter.byIds(emptyList()))
    }

    @Test
    fun `the guide comes without descriptions, and not again while it hasn't changed`() = runTest {
        val client = FlexarrClient(server.url("/"), apiKey = "key")
        server.enqueue(MockResponse.Builder().addHeader("Content-Type", "application/json").addHeader("ETag", "W/\"abc\"").body(guide).build())
        server.enqueue(MockResponse.Builder().code(304).build())
        val from = Instant.parse("2026-11-07T10:00:00Z")
        val first = client.liveGuide(from, 6, GuideFilter(mapOf("favourites" to "true")), descriptions = false, etag = null)
        assertEquals("a", first.guide!!.channels.single().id)
        assertEquals("W/\"abc\"", first.etag)
        val asked = server.takeRequest()
        assertEquals("2026-11-07T10:00:00Z", asked.url.queryParameter("from"))
        assertEquals("6", asked.url.queryParameter("hours"))
        assertEquals("true", asked.url.queryParameter("favourites"))
        assertEquals("false", asked.url.queryParameter("descriptions"))
        assertNull(asked.headers["If-None-Match"])

        val again = client.liveGuide(from, 6, GuideFilter(mapOf("favourites" to "true")), descriptions = false, etag = first.etag)
        assertNull(again.guide)
        assertEquals("W/\"abc\"", again.etag)
        assertEquals("W/\"abc\"", server.takeRequest().headers["If-None-Match"])
    }

    @Test
    fun `a server before the guide's new parameters sends it whole, as before`() = runTest {
        val client = FlexarrClient(server.url("/"), apiKey = "key")
        server.enqueue(MockResponse.Builder().addHeader("Content-Type", "application/json").body(guide).build())
        val fetched = client.liveGuide(Instant.parse("2026-11-07T10:00:00Z"), 6, GuideFilter.Everything, descriptions = true, etag = null)
        assertEquals(1, fetched.guide!!.channels.size)
        assertNull(fetched.etag)
        assertNull(server.takeRequest().url.queryParameter("descriptions"))
    }

    @Test
    fun `a programme's description is fetched by itself, and missing from older servers`() = runTest {
        val client = FlexarrClient(server.url("/"), apiKey = "key")
        server.enqueue(MockResponse.Builder().addHeader("Content-Type", "application/json")
            .body("""{"id":7,"start":"2026-11-07T10:00:00Z","end":"2026-11-07T11:00:00Z","title":"News","description":"The day's news."}""").build())
        assertEquals("The day's news.", client.liveProgramme(7)!!.description)
        assertEquals("/api/v1/livetv/programmes/7", server.takeRequest().url.encodedPath)
        server.enqueue(MockResponse.Builder().code(404).build())
        assertNull(client.liveProgramme(7))
        server.enqueue(MockResponse.Builder().addHeader("Content-Type", "text/html").body("<html></html>").build())
        assertNull(client.liveProgramme(7))
    }

    private fun status(refreshing: Boolean, finished: Int, error: String? = null) =
        MockResponse.Builder().addHeader("Content-Type", "application/json").body(
            """{"configured":true,"channels":1,"refreshing":$refreshing,"refreshesFinished":$finished${if (error != null) ""","error":"$error"""" else ""}}""",
        ).build()

    private fun answers(vararg responses: MockResponse) = responses.forEach(server::enqueue)

    @Test
    fun `a refresh still running when the wait runs out isn't done, and isn't its last result's failure`() = runTest {
        val client = FlexarrClient(server.url("/"), apiKey = "key")
        answers(status(false, 4, error = "Last time"), MockResponse.Builder().code(202).build())
        repeat(181) { answers(status(true, 4, error = "Last time")) }
        val end = client.refreshLiveTv()
        assertTrue(end.stillGoing)
        assertNull(end.problem)
        assertTrue(end.status!!.refreshing)
    }

    @Test
    fun `a refresh that finishes says how it went`() = runTest {
        val client = FlexarrClient(server.url("/"), apiKey = "key")
        answers(status(false, 4), MockResponse.Builder().code(202).build(), status(true, 4), status(false, 5))
        val fine = client.refreshLiveTv()
        assertFalse(fine.stillGoing)
        assertNull(fine.problem)

        answers(status(false, 5), MockResponse.Builder().code(202).build(), status(false, 6, error = "No sources"))
        assertEquals("No sources", client.refreshLiveTv().problem)
    }

    @Test
    fun `a server that stops answering once asked is said to have stopped answering`() = runTest {
        val client = FlexarrClient(server.url("/"), apiKey = "key")
        answers(status(false, 4), MockResponse.Builder().code(202).build())
        repeat(3) { answers(MockResponse.Builder().code(502).build()) }
        val end = client.refreshLiveTv()
        assertTrue(end.noAnswer)
        assertFalse(end.stillGoing)
    }

    @Test
    fun `a look or two that fail don't end the wait`() = runTest {
        val client = FlexarrClient(server.url("/"), apiKey = "key")
        answers(status(false, 4), MockResponse.Builder().code(202).build())
        repeat(2) { answers(MockResponse.Builder().code(502).build()) }
        answers(status(false, 5))
        assertFalse(client.refreshLiveTv().noAnswer)
    }

    @Test
    fun `a refresh that didn't ask the sources says so, and older servers don't`() {
        assertTrue(FlexarrClient.json.decodeFromString(LiveTvStatus.serializer(), """{"configured":true,"channels":1,"downloadSkipped":true}""").downloadSkipped)
        assertFalse(FlexarrClient.json.decodeFromString(LiveTvStatus.serializer(), """{"configured":true,"channels":1}""").downloadSkipped)
    }
}
