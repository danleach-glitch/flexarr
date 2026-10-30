package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class RecentSearchesTest {
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

    private fun noContent() = MockResponse.Builder().code(204).build()

    @Test
    fun `reads the searches and titles, and remembers a pick`() = runTest {
        val client = FlexarrClient(server.url("/"))
        server.enqueue(
            json(
                """{"queries":["fargo","heat"],"items":[{"id":"i1","libraryId":"l","kind":"Movie","title":"Heat","childCount":0,
                "artwork":{},"userData":{"positionSeconds":0,"watched":false,"playCount":0}}]}""",
            ),
        )
        server.enqueue(noContent())
        server.enqueue(noContent())

        val recents = client.recentSearches()
        assertEquals(listOf("fargo", "heat"), recents.queries)
        assertEquals("Heat", recents.items.single().title)
        assertEquals("/api/v1/search/recent", server.takeRequest().target)

        client.recordSearch("heat", "i1")
        assertEquals("""{"query":"heat","itemId":"i1"}""", server.takeRequest().body?.utf8())
        // A title reopened from the recents is just its id.
        client.recordSearch(null, "i1")
        assertEquals("""{"itemId":"i1"}""", server.takeRequest().body?.utf8())
    }

    @Test
    fun `forgets a search, a title, or everything`() = runTest {
        val client = FlexarrClient(server.url("/"))
        repeat(3) { server.enqueue(noContent()) }

        client.forgetSearch("the bear")
        server.takeRequest().let {
            assertEquals("DELETE", it.method)
            assertEquals("/api/v1/search/recent?query=the%20bear", it.target)
        }
        client.forgetRecentItem("i1")
        assertEquals("/api/v1/search/recent?itemId=i1", server.takeRequest().target)
        client.clearRecentSearches()
        assertEquals("/api/v1/search/recent", server.takeRequest().target)
    }

    @Test
    fun `a server from before recent searches has none`() = runTest {
        server.enqueue(MockResponse.Builder().code(404).build())
        assertTrue(FlexarrClient(server.url("/")).recentSearches().isEmpty)
    }
}
