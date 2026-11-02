package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
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

    @Test
    fun `the shelves' hero books and their overviews come from servers from 0_59`() = runTest {
        server.enqueue(
            json(
                """{"featured":[{"id":"b1","libraryId":"lib","kind":"Audiobook","title":"Dune","childCount":0,
                   "artwork":{"poster":"/api/v1/images/b1/poster"},"userData":{"positionSeconds":600,"watched":false,"playCount":0}},
                   {"id":"b2","libraryId":"lib","kind":"Audiobook","title":"Emma","childCount":0,
                   "artwork":{"poster":"/api/v1/images/b2/poster"},"userData":{"positionSeconds":0,"watched":false,"playCount":0}}],
                   "featuredOverviews":{"b1":"Spice.","b2":null}}""",
            ),
        )
        val shelves = client().bookShelves("lib")
        assertEquals(listOf("b1", "b2"), shelves.featured.map { it.id })
        assertEquals("Spice.", shelves.featuredOverviews["b1"])
        assertNull(shelves.featuredOverviews["b2"])
    }

    @Test
    fun `a server before 0_59 has no hero books`() = runTest {
        server.enqueue(json("""{"genres":[]}"""))
        val shelves = client().bookShelves("lib")
        assertTrue(shelves.featured.isEmpty())
        assertTrue(shelves.featuredOverviews.isEmpty())
    }
}
