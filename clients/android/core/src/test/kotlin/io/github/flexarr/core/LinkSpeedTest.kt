package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import okio.Buffer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LinkSpeedTest {
    private class Kept : LinkSpeed.Memory {
        var text: String? = null
        override fun read() = text
        override fun write(text: String) {
            this.text = text
        }
    }

    @Test
    fun `is the median of recent big segments, once there are a few`() {
        val speed = LinkSpeed({ 0L })
        assertNull(speed.current("a"))
        speed.sample("a", 1_000_000, 2000) // 4 Mbps
        speed.sample("a", 1_000_000, 1000) // 8 Mbps
        assertNull(speed.current("a")) // two say nothing
        speed.sample("a", 1_000_000, 500) // 16 Mbps
        speed.sample("a", 1_000_000, 250) // 32 Mbps
        assertEquals(8_000_000L, speed.current("a")) // the lower middle of four
        speed.sample("a", 1_000_000, 125) // 64 Mbps
        assertEquals(16_000_000L, speed.current("a"))
        assertNull(speed.current("b")) // another address knows nothing of it
    }

    @Test
    fun `ignores small transfers`() {
        val speed = LinkSpeed({ 0L })
        speed.sample("a", LinkSpeed.MIN_SAMPLE_BYTES - 1, 10)
        speed.sample("a", 1_000_000, 0)
        assertNull(speed.current("a"))
    }

    @Test
    fun `a speed test says something by itself`() {
        val speed = LinkSpeed({ 0L })
        speed.tested("a", 5_000_000)
        assertEquals(5_000_000L, speed.fresh("a"))
    }

    @Test
    fun `is fresh for half an hour, then a starting point for a day`() {
        var clock = 0L
        val speed = LinkSpeed({ clock })
        repeat(3) { speed.sample("a", 1_000_000, 1000) }
        clock = LinkSpeed.FRESH_MS + 1
        assertNull(speed.fresh("a"))
        assertEquals(8_000_000L, speed.current("a"))
        clock = LinkSpeed.KEPT_MS + 1
        assertNull(speed.current("a"))
    }

    @Test
    fun `is kept between launches`() {
        val kept = Kept()
        var clock = 1_000L
        LinkSpeed({ clock }, kept).tested("a", 6_000_000)
        clock += LinkSpeed.FRESH_MS + 1
        val next = LinkSpeed({ clock }, kept)
        assertNull(next.fresh("a"))
        assertEquals(6_000_000L, next.current("a"))
        clock += LinkSpeed.KEPT_MS
        assertNull(LinkSpeed({ clock }, kept).current("a"))
        // Something unreadable is nothing.
        kept.text = "nonsense"
        assertNull(LinkSpeed({ clock }, kept).current("a"))
    }

    @Test
    fun `keeps only the places used lately`() {
        var clock = 0L
        val speed = LinkSpeed({ clock })
        repeat(LinkSpeed.PLACES + 1) { clock++; speed.tested("p$it", 1_000_000) }
        assertNull(speed.current("p0"))
        assertNotNull(speed.current("p${LinkSpeed.PLACES}"))
    }

    @Test
    fun `speed tests at most every ten minutes, sooner on another network`() {
        var clock = 0L
        val speed = LinkSpeed({ clock })
        assertTrue(speed.testDue("a"))
        speed.testStarted("a")
        clock += 60_000
        assertFalse(speed.testDue("a"))
        assertTrue(speed.testDue("b"))
        speed.networkChanged()
        assertTrue(speed.testDue("a"))
        speed.testStarted("a")
        clock += LinkSpeed.TEST_INTERVAL_MS
        assertTrue(speed.testDue("a"))
        // The title page's only when there's no fresh reading.
        speed.tested("a", 1_000_000)
        assertFalse(speed.testDue("a", onlyIfStale = true))
    }

    @Test
    fun `the speed test times the bytes and asks for the size`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().body(Buffer().write(ByteArray(1_000_000))).build())
            val bps = FlexarrClient(server.url("/"), apiKey = "key").speedTest(1_000_000)
            assertNotNull(bps)
            assertTrue(bps!! > 0)
            val request = server.takeRequest()
            assertEquals("/api/v1/speedtest", request.url.encodedPath)
            assertEquals("1000000", request.url.queryParameter("bytes"))
            assertEquals("key", request.headers["X-Api-Key"])
        }
    }

    @Test
    fun `an older server without the speed test is no reading`() = runTest {
        MockWebServer().use { server ->
            server.start()
            server.enqueue(MockResponse.Builder().code(404).build())
            assertNull(FlexarrClient(server.url("/"), apiKey = "key").speedTest(1_000_000))
        }
    }
}
