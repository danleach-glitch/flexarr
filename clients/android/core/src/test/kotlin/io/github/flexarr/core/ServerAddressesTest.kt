package io.github.flexarr.core

import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import okhttp3.HttpUrl.Companion.toHttpUrl
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.IOException
import java.net.ServerSocket

class ServerAddressesTest {
    private lateinit var away: MockWebServer

    /** An address nothing listens on: connecting is refused at once. */
    private val home = ServerSocket(0).use { "http://127.0.0.1:${it.localPort}/".toHttpUrl() }

    @Before
    fun start() {
        away = MockWebServer()
        away.start()
    }

    @After
    fun stop() {
        away.close()
    }

    private fun json(body: String) = MockResponse.Builder().addHeader("Content-Type", "application/json").body(body).build()

    @Test
    fun `a GET that can't reach home is answered from away, and later requests go there`() = runTest {
        val addresses = ServerAddresses(home, away.url("/"))
        val client = FlexarrClient(addresses, apiKey = "token")
        away.enqueue(json("[]"))
        away.enqueue(json("[]"))

        client.libraries()
        assertTrue(addresses.isAway)
        client.libraries()

        assertEquals(2, away.requestCount)
        assertEquals("token", away.takeRequest().headers["X-Api-Key"])
    }

    @Test
    fun `a write moves over only after home refused the connection`() = runTest {
        val addresses = ServerAddresses(home, away.url("/"))
        away.enqueue(MockResponse.Builder().code(204).build())

        FlexarrClient(addresses).setWatched("1", true)

        assertEquals("/api/v1/items/1/watched", away.takeRequest().url.encodedPath)
        assertEquals(1, away.requestCount)
    }

    @Test
    fun `the same address twice is one address`() {
        val addresses = ServerAddresses(home, home)
        assertEquals(null, addresses.alternate(home))
        assertFalse(addresses.isAway)
    }

    @Test
    fun `home is tried again every few minutes while away`() {
        val addresses = ServerAddresses(home, away.url("/"))
        assertFalse(addresses.shouldCheckHome(now = 0))
        addresses.reached(away.url("/"))
        val left = System.currentTimeMillis()
        assertFalse(addresses.shouldCheckHome(now = left + 1_000))
        assertTrue(addresses.shouldCheckHome(now = left + ServerAddresses.HOME_CHECK_INTERVAL_MS + 1))
        assertFalse(addresses.shouldCheckHome(now = left + ServerAddresses.HOME_CHECK_INTERVAL_MS + 2))
    }

    @Test
    fun `home is sent the sign-in only after it answers with the server's id`() = runTest {
        val atHome = MockWebServer().apply { start() }
        try {
            val addresses = ServerAddresses(atHome.url("/"), away.url("/")).apply { serverId = "abc" }
            atHome.enqueue(json("""{"open":false,"serverId":"abc"}"""))
            atHome.enqueue(json("[]"))

            FlexarrClient(addresses, apiKey = "token").libraries()

            val probe = atHome.takeRequest()
            assertEquals("/api/v1/setup", probe.url.encodedPath)
            assertEquals(null, probe.headers["X-Api-Key"])
            assertEquals("token", atHome.takeRequest().headers["X-Api-Key"])
            assertFalse(addresses.isAway)
            assertEquals(0, away.requestCount)
        } finally {
            atHome.close()
        }
    }

    @Test
    fun `another server at the home address never sees the sign-in`() = runTest {
        val elsewhere = MockWebServer().apply { start() }
        try {
            val addresses = ServerAddresses(elsewhere.url("/"), away.url("/")).apply { serverId = "abc" }
            elsewhere.enqueue(json("""{"open":false,"serverId":"someone-else"}"""))
            away.enqueue(json("[]"))
            away.enqueue(json("[]"))

            val client = FlexarrClient(addresses, apiKey = "token")
            client.libraries()
            client.libraries()

            assertEquals(1, elsewhere.requestCount)
            assertEquals(null, elsewhere.takeRequest().headers["X-Api-Key"])
            assertEquals("token", away.takeRequest().headers["X-Api-Key"])
            assertTrue(addresses.isAway)
        } finally {
            elsewhere.close()
        }
    }

    @Test
    fun `with no other address, a stranger at home is unreachable rather than signed in to`() = runTest {
        val elsewhere = MockWebServer().apply { start() }
        try {
            val addresses = ServerAddresses(elsewhere.url("/"), null).apply { serverId = "abc" }
            elsewhere.enqueue(json("""{"open":false}"""))

            val failed = runCatching { FlexarrClient(addresses, apiKey = "token").libraries() }.exceptionOrNull()

            assertTrue(failed is FlexarrException.Unreachable)
            assertEquals(1, elsewhere.requestCount)
        } finally {
            elsewhere.close()
        }
    }

    @Test
    fun `the command stream and a download's file go away rather than to a stranger at home`() = runTest {
        val elsewhere = MockWebServer().apply { start() }
        try {
            val addresses = ServerAddresses(elsewhere.url("/"), away.url("/")).apply { serverId = "abc" }
            // Each of the three asks home who it is first, without the sign-in.
            repeat(3) { elsewhere.enqueue(json("""{"open":false,"serverId":"someone-else"}""")) }
            away.enqueue(MockResponse.Builder().addHeader("Content-Type", "text/event-stream").body(": hello\n\n").build())
            val client = FlexarrClient(addresses, apiKey = "token")

            client.deviceEvents().toList()
            val film = client.downloadFileRequest("abc")
            val book = client.bookFileRequest("/api/v1/items/book/file/0", fromByte = 10)!!

            // Counted before it's taken: taking a request that never came would wait for ever.
            assertEquals(1, away.requestCount)
            val stream = away.takeRequest()
            assertEquals("/api/v1/devices/commands", stream.url.encodedPath)
            assertEquals("token", stream.headers["X-Api-Key"])
            assertEquals(away.port, film.url.port)
            assertEquals("/api/v1/offline/abc/file", film.url.encodedPath)
            assertEquals(away.port, book.url.port)
            assertEquals("/api/v1/items/book/file/0", book.url.encodedPath)
            assertEquals("bytes=10-", book.header("Range"))
            assertEquals(3, elsewhere.requestCount)
            repeat(3) {
                val probe = elsewhere.takeRequest()
                assertEquals("/api/v1/setup", probe.url.encodedPath)
                assertEquals(null, probe.headers["X-Api-Key"])
            }
        } finally {
            elsewhere.close()
        }
    }

    @Test
    fun `with no other address, the command stream and downloads wait rather than sign in to a stranger`() = runTest {
        val elsewhere = MockWebServer().apply { start() }
        try {
            val addresses = ServerAddresses(elsewhere.url("/"), null).apply { serverId = "abc" }
            repeat(3) { elsewhere.enqueue(json("""{"open":false,"serverId":"someone-else"}""")) }
            val client = FlexarrClient(addresses, apiKey = "token")

            assertTrue(runCatching { client.deviceEvents().toList() }.exceptionOrNull() is FlexarrException.Unreachable)
            assertTrue(runCatching { client.downloadFileRequest("abc") }.exceptionOrNull() is IOException)
            assertTrue(runCatching { client.bookFileRequest("/api/v1/items/book/file/0") }.exceptionOrNull() is IOException)

            assertEquals(3, elsewhere.requestCount)
            repeat(3) { assertEquals(null, elsewhere.takeRequest().headers["X-Api-Key"]) }
        } finally {
            elsewhere.close()
        }
    }

    @Test
    fun `at home, the command stream and a download's file stay at home once it has answered with the server's id`() = runTest {
        val atHome = MockWebServer().apply { start() }
        try {
            val addresses = ServerAddresses(atHome.url("/"), away.url("/")).apply { serverId = "abc" }
            atHome.enqueue(json("""{"open":false,"serverId":"abc"}"""))
            atHome.enqueue(MockResponse.Builder().addHeader("Content-Type", "text/event-stream").body(": hello\n\n").build())
            val client = FlexarrClient(addresses, apiKey = "token")

            client.deviceEvents().toList()
            val film = client.downloadFileRequest("abc")
            val book = client.bookFileRequest("/api/v1/items/book/file/0")!!

            // Asked once: the answer holds until the network changes.
            assertEquals(2, atHome.requestCount)
            assertEquals(0, away.requestCount)
            assertEquals(null, atHome.takeRequest().headers["X-Api-Key"])
            assertEquals("token", atHome.takeRequest().headers["X-Api-Key"])
            assertEquals(atHome.port, film.url.port)
            assertEquals(atHome.port, book.url.port)
            assertEquals("token", book.header("X-Api-Key"))
            // Somewhere that isn't the server's is never sent the sign-in.
            assertEquals(null, client.bookFileRequest("http://example.com/file"))
        } finally {
            atHome.close()
        }
    }

    @Test
    fun `a new network asks home again`() {
        val addresses = ServerAddresses("http://192.168.1.2:8383/".toHttpUrl(), away.url("/")).apply { serverId = "abc" }
        assertFalse(addresses.homeTrustedNow)
        addresses.homeVerified()
        assertTrue(addresses.homeTrustedNow)
        ServerAddresses.networkChangedEverywhere() // not in the registry: only shared() ones are
        addresses.networkChanged()
        assertFalse(addresses.homeTrustedNow)
        assertTrue(ServerAddresses("https://flexarr.example.com/".toHttpUrl(), null).apply { serverId = "abc" }.homeTrustedNow)
    }
}
