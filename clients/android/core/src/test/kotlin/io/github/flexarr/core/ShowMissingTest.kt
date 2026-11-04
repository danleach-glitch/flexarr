package io.github.flexarr.core

import java.time.Instant
import java.time.ZoneOffset
import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/** A show's and a book series' gaps: what the server says, the words for them, and older servers. */
class ShowMissingTest {
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

    @Test
    fun `a show's gaps, counts and what's coming read from the server`() = runTest {
        val client = FlexarrClient(server.url("/"))
        server.enqueue(json("""
            {"source":"sonarr","canRequest":true,"canSearch":false,
             "seasons":[{"seasonNumber":2,"name":null,"episodes":8,"year":2021,"poster":null,"availability":"Requested"}],
             "episodes":[{"seasonNumber":1,"episodeNumber":3,"title":"Three","airDate":"2020-01-03T00:00:00Z","still":null,"monitored":false},
                         {"seasonNumber":1,"episodeNumber":5,"title":null,"airDate":"2020-01-05T00:00:00Z","still":null,"monitored":true}],
             "upcoming":[{"seasonNumber":3,"episodeNumber":1,"title":"TBA","airDate":"2026-11-12T21:00:00Z","still":null,"monitored":true}],
             "counts":[{"seasonNumber":1,"have":8,"aired":10},{"seasonNumber":3,"have":2,"aired":2}],
             "future":"ignored"}
        """.trimIndent()))

        val gaps = client.showMissing("show1")

        assertEquals("/api/v1/items/show1/missing", server.takeRequest().target)
        val season = gaps.seasons.single()
        assertEquals("Season 2", season.title)
        assertEquals("8 episodes, 2021", season.detail)
        assertTrue(season.requested)
        assertEquals(listOf(1 to listOf("S1E3 · Three", "S1E5")), gaps.episodesBySeason.map { (s, list) -> s to list.map { it.label } })
        assertEquals("8 of 10", gaps.countLabel(1))
        assertNull(gaps.countLabel(3))
        assertEquals("Airs 12 Nov", ShowMissing.airsLabel(gaps.upcoming.single().airDate!!, ZoneOffset.UTC, Instant.parse("2026-11-02T12:00:00Z")))
        assertEquals("Airs 12 Nov 2027", ShowMissing.airsLabel(Instant.parse("2027-11-12T12:00:00Z"), ZoneOffset.UTC, Instant.parse("2026-11-02T12:00:00Z")))
    }

    @Test
    fun `older servers have no gaps, and requests name their seasons`() = runTest {
        val client = FlexarrClient(server.url("/"))
        server.enqueue(MockResponse.Builder().code(404).build())
        server.enqueue(MockResponse.Builder().code(404).build())
        server.enqueue(MockResponse.Builder().code(202).build())
        server.enqueue(MockResponse.Builder().code(202).build())

        assertEquals(ShowMissing.None, client.showMissing("old"))
        server.takeRequest()
        assertEquals(BookSeriesMissing(), client.bookSeriesMissing("s1"))
        assertEquals("/api/v1/book-series/s1/missing", server.takeRequest().target)
        client.requestSeasons(9001, listOf(2))
        assertEquals("""{"mediaType":"tv","tmdbId":9001,"seasons":[2]}""", server.takeRequest().body!!.utf8())
        client.searchMissing("show1", 1)
        val search = server.takeRequest()
        assertEquals("/api/v1/items/show1/missing/search", search.target)
        assertEquals("""{"seasonNumber":1}""", search.body!!.utf8())
    }

    @Test
    fun `older servers answering with their web page have no gaps either`() = runTest {
        val client = FlexarrClient(server.url("/"))
        val page = MockResponse.Builder().code(200).addHeader("Content-Type", "text/html").body("<!doctype html><html></html>").build()
        server.enqueue(page)
        server.enqueue(page)

        assertEquals(ShowMissing.None, client.showMissing("old"))
        assertEquals(BookSeriesMissing(), client.bookSeriesMissing("s1"))
    }

    @Test
    fun `a missing book says its number and year`() {
        assertEquals("Book 3, 2022", MissingBook("A", "The Bullet That Missed", "3", year = 2022).detail)
        assertEquals("2022", MissingBook("A", "Untitled", null, year = 2022).detail)
    }
}
