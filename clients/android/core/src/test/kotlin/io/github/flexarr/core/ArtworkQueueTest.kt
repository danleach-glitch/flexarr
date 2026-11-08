package io.github.flexarr.core

import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import okhttp3.Call
import okhttp3.Callback
import okhttp3.Request
import okhttp3.Response
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotSame
import org.junit.Assert.assertSame
import org.junit.Test
import java.io.IOException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** Artwork waits in a queue of its own ([FlexarrClient.artworkHttp]). */
class ArtworkQueueTest {
    @Test
    fun `artwork has its own queue over the shared connections`() {
        assertNotSame(FlexarrClient.sharedHttp.dispatcher, FlexarrClient.artworkHttp.dispatcher)
        assertSame(FlexarrClient.sharedHttp.connectionPool, FlexarrClient.artworkHttp.connectionPool)
        // The family guest's tunnel still carries it.
        assertEquals(FlexarrClient.sharedHttp.interceptors, FlexarrClient.artworkHttp.interceptors)
    }

    @Test
    fun `an API call doesn't wait behind posters still loading`() = runBlocking {
        val postersMayAnswer = CountDownLatch(1)
        MockWebServer().use { server ->
            server.dispatcher = object : Dispatcher() {
                override fun dispatch(request: RecordedRequest): MockResponse =
                    if (request.url.encodedPath.startsWith("/art/")) {
                        postersMayAnswer.await(30, TimeUnit.SECONDS)
                        MockResponse.Builder().body("picture").build()
                    } else {
                        MockResponse.Builder().addHeader("Content-Type", "application/json").body("[]").build()
                    }
            }
            server.start()
            // More posters than one server is sent at a time, none of them answered yet.
            val posters = (1..8).map { FlexarrClient.artworkHttp.newCall(Request.Builder().url(server.url("/art/$it")).build()) }
            try {
                posters.forEach {
                    it.enqueue(object : Callback {
                        override fun onFailure(call: Call, e: IOException) = Unit
                        override fun onResponse(call: Call, response: Response) = response.close()
                    })
                }
                val libraries = withTimeout(10_000) { FlexarrClient(server.url("/"), apiKey = "key").libraries() }
                assertEquals(emptyList<Library>(), libraries)
            } finally {
                postersMayAnswer.countDown()
                posters.forEach { it.cancel() }
            }
        }
    }
}
