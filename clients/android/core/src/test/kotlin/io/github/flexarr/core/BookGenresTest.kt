package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test

class BookGenresTest {
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

    private fun client() = FlexarrClient(server.url("/"), apiKey = "device-token")

    private fun json(body: String) = MockResponse.Builder().code(200).addHeader("Content-Type", "application/json").body(body).build()

    @Test
    fun `genres come A to Z from their own call`() = runTest {
        server.enqueue(json("""[{"name":"science fiction","count":2},{"name":"Biography","count":1}]"""))
        assertEquals(listOf("Biography", "science fiction"), client().bookGenres("lib").map { it.name })
        assertEquals("/api/v1/libraries/lib/book-genres", server.takeRequest().url.encodedPath)
    }

    @Test
    fun `a server before 0_58 answers with its web page, so the shelves' genres are used`() = runTest {
        server.enqueue(MockResponse.Builder().code(200).addHeader("Content-Type", "text/html").body("<html></html>").build())
        server.enqueue(json("""{"genres":[{"name":"Mystery","count":3}]}"""))
        assertEquals(listOf("Mystery"), client().bookGenres("lib").map { it.name })
        server.takeRequest()
        assertEquals("/api/v1/libraries/lib/book-shelves", server.takeRequest().url.encodedPath)
    }
}
