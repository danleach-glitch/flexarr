package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
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
}
