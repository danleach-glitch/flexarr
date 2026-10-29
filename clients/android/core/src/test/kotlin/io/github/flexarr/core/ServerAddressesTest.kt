package io.github.flexarr.core

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
}
