package io.github.flexarr.core

import kotlinx.coroutines.runBlocking
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
import java.util.concurrent.TimeUnit

/**
 * A household device that joined by invite, with its server's home address: home while it proves it's the server, the
 * tunnel (here a plain server standing in for it) when it doesn't (docs/remote-guests.md, "Household devices at home").
 */
class HouseholdRouteTest {
    private lateinit var atHome: MockWebServer
    private lateinit var tunnel: MockWebServer

    @Before
    fun start() {
        atHome = MockWebServer().apply { start() }
        tunnel = MockWebServer().apply { start() }
    }

    @After
    fun stop() {
        atHome.close()
        tunnel.close()
    }

    private fun json(body: String) = MockResponse.Builder().addHeader("Content-Type", "application/json").body(body).build()

    private fun pair() = ServerAddresses(atHome.url("/"), tunnel.url("/"), awayIsTunnel = true).apply { serverId = "abc" }

    @Test
    fun `home gets no key before it proves it's the server, and the tunnel carries on`() = runTest {
        val addresses = pair()
        atHome.enqueue(json("""{"open":false,"serverId":"someone-else"}"""))
        tunnel.enqueue(json("[]"))

        FlexarrClient(addresses, apiKey = "token").libraries()

        assertEquals(1, atHome.requestCount)
        val probe = atHome.takeRequest()
        assertEquals("/api/v1/setup", probe.url.encodedPath)
        assertNull(probe.headers["X-Api-Key"])
        assertEquals("token", tunnel.takeRequest().headers["X-Api-Key"])
        assertTrue(addresses.onTunnel)
    }

    @Test
    fun `home proven, requests stay there and the tunnel isn't asked`() = runTest {
        val addresses = pair()
        var announced = 0
        ServerAddresses.onRouteChanged = { if (it === addresses) announced++ }
        try {
            atHome.enqueue(json("""{"open":false,"serverId":"abc"}"""))
            atHome.enqueue(json("[]"))
            FlexarrClient(addresses, apiKey = "token").libraries()
            assertTrue(addresses.homeProven)
            assertFalse(addresses.onTunnel)
            assertEquals(0, tunnel.requestCount)
            // Home proving itself is a change of route the app hears about (to stop the tunnel).
            assertEquals(1, announced)
        } finally {
            ServerAddresses.onRouteChanged = null
        }
    }

    @Test
    fun `a slow answer at home isn't raced to the tunnel`() = runTest {
        val addresses = pair().also { it.homeVerified() }
        atHome.enqueue(json("[]").newBuilder().headersDelay(ServerAddresses.ALTERNATE_DELAY_MS + 1_000, TimeUnit.MILLISECONDS).build())

        FlexarrClient(addresses, apiKey = "token").libraries()

        assertEquals(0, tunnel.requestCount)
        assertFalse(addresses.isAway)
    }

    @Test
    fun `a refusal at home goes through the tunnel and never signs out`() = runTest {
        val addresses = pair().also { it.homeVerified() }
        var refused = false
        atHome.enqueue(MockResponse.Builder().code(401).build())
        tunnel.enqueue(json("[]"))

        FlexarrClient(addresses, apiKey = "token", onUnauthorized = { refused = true }).libraries()

        assertFalse(refused)
        assertEquals(1, tunnel.requestCount)
        assertTrue(addresses.onTunnel)
        // Home is left alone on this network, and asked again on the next.
        assertFalse(addresses.homeTrustedNow)
        assertFalse(addresses.shouldCheckHome(now = System.currentTimeMillis() + ServerAddresses.HOME_CHECK_INTERVAL_MS + 1))
        addresses.networkChanged()
        assertTrue(addresses.shouldCheckHome())
    }

    @Test
    fun `a refusal from a server with no tunnel is the server's word`() = runTest {
        val addresses = ServerAddresses(atHome.url("/"), tunnel.url("/"))
        var refused = false
        atHome.enqueue(MockResponse.Builder().code(401).build())

        val failed = runCatching { FlexarrClient(addresses, apiKey = "token", onUnauthorized = { refused = true }).libraries() }.exceptionOrNull()

        assertTrue(failed is FlexarrException.Unauthorized)
        assertTrue(refused)
        assertEquals(0, tunnel.requestCount)
    }

    @Test
    fun `starting away asks home at the first request, and a new network asks again at once`() {
        val addresses = pair().also { it.startAway() }
        assertTrue(addresses.onTunnel)
        assertTrue(addresses.shouldCheckHome())
        assertFalse(addresses.shouldCheckHome())
        addresses.networkChanged()
        assertTrue(addresses.shouldCheckHome())
        // The tunnel is the app's own: a new network doesn't make it prove itself again.
        assertTrue(addresses.remoteTrustedNow)
    }

    @Test
    fun `a download that loses home carries on through the tunnel`() {
        val addresses = pair()
        addresses.homeMissed(atHome.url("/api/v1/offline/1/file"))
        assertTrue(addresses.onTunnel)
        // Without the tunnel as the away address, nothing moves.
        val plain = ServerAddresses(atHome.url("/"), tunnel.url("/"))
        plain.homeMissed(atHome.url("/api/v1/offline/1/file"))
        assertFalse(plain.isAway)
    }

    @Test
    fun `a new network unproves home and says so`() {
        val addresses = pair().also { it.homeVerified() }
        var announced = 0
        ServerAddresses.onRouteChanged = { if (it === addresses) announced++ }
        try {
            addresses.networkChanged()
            assertFalse(addresses.homeProven)
            assertEquals(1, announced)
            // Not proven to begin with: nothing to say.
            addresses.networkChanged()
            assertEquals(1, announced)
        } finally {
            ServerAddresses.onRouteChanged = null
        }
    }

    @Test
    fun `a blip at home that home still answers through stays at home`() = runBlocking {
        val addresses = pair().also { it.homeVerified() }
        atHome.enqueue(json("""{"open":false,"serverId":"abc"}"""))

        FlexarrClient(addresses, apiKey = "token").homeMissed(atHome.url("/api/v1/offline/1/file"))

        assertFalse(addresses.isAway)
        assertEquals(0, tunnel.requestCount)
        assertEquals("/api/v1/setup", atHome.takeRequest().url.encodedPath)
    }

    @Test
    fun `a download that loses home and home doesn't answer moves to the tunnel`() = runBlocking {
        val addresses = pair().also { it.homeVerified() }
        atHome.enqueue(json("""{"open":false,"serverId":"someone-else"}"""))

        FlexarrClient(addresses, apiKey = "token").homeMissed(atHome.url("/api/v1/offline/1/file"))

        assertTrue(addresses.onTunnel)
    }
}
