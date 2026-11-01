package io.github.flexarr.core

import kotlinx.coroutines.runBlocking
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import org.junit.After
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.net.ServerSocket
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

/** The quick check offline mode rests on, and the hook that has the app check when a request can't connect. */
class ReachabilityTest {
    private lateinit var server: MockWebServer

    /** An address nothing listens on: connecting is refused at once. */
    private val nobody = ServerSocket(0).use { "http://127.0.0.1:${it.localPort}/".toHttpUrl() }

    @Before
    fun start() {
        server = MockWebServer()
        server.start()
    }

    @After
    fun stop() {
        server.close()
        FlexarrClient.onCouldNotConnect = null
    }

    @Test
    fun `any answer counts, even a refusal`() = runBlocking {
        server.enqueue(MockResponse.Builder().code(401).build())
        assertTrue(FlexarrClient(server.url("/")).isReachable(2_000))
        assertTrue(server.takeRequest().url.encodedPath == "/api/v1/system/status")
    }

    @Test
    fun `the away address answering is used from then on`() = runBlocking {
        server.enqueue(MockResponse.Builder().code(200).body("{}").build())
        val addresses = ServerAddresses(nobody, server.url("/"))
        assertTrue(FlexarrClient(addresses).isReachable(2_000))
        assertTrue(addresses.isAway)
    }

    @Test
    fun `nothing listening says so at once`() = runBlocking {
        val started = System.currentTimeMillis()
        assertFalse(FlexarrClient(nobody).isReachable(4_000))
        assertTrue(System.currentTimeMillis() - started < 2_000)
    }

    @Test
    fun `a server that never answers is given up on after the limit`() = runBlocking {
        server.enqueue(MockResponse.Builder().headersDelay(10, TimeUnit.SECONDS).build())
        val started = System.currentTimeMillis()
        assertFalse(FlexarrClient(server.url("/")).isReachable(300))
        assertTrue(System.currentTimeMillis() - started < 2_000)
    }

    @Test
    fun `a request that can't connect is reported, an error answer isn't`() = runBlocking {
        val seen = CopyOnWriteArrayList<HttpUrl>()
        FlexarrClient.onCouldNotConnect = { seen += it }
        server.enqueue(MockResponse.Builder().code(503).build())

        runCatching { FlexarrClient(nobody).libraries() }
        runCatching { FlexarrClient(server.url("/")).libraries() }

        assertTrue(nobody in seen)
        assertFalse(server.url("/") in seen)
    }
}
