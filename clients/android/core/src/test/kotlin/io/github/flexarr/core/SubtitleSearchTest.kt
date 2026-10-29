package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class SubtitleSearchTest {
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
    fun `searches for a file and saves the pick beside it`() = runTest {
        val client = FlexarrClient(server.url("/"))
        server.enqueue(
            json(
                """{"results":[{"id":"r1","provider":"opensubtitlescom","language":"en","languageLabel":"English",
                "release":"Film.2020.1080p","hearingImpaired":false,"forced":false,"score":97,"matchesFile":true,"uploader":null}]}""",
            ),
        )
        server.enqueue(json("""{"key":"ext:2","label":"English"}"""))

        val results = client.searchSubtitles("item", "file")
        assertEquals("English", results.single().languageLabel)
        assertTrue(results.single().matchesFile)
        assertEquals("/api/v1/items/item/subtitles/search?fileId=file", server.takeRequest().target)

        val saved = client.downloadSubtitles("item", "file", result = "r1")
        assertEquals("ext:2", saved.key)
        assertEquals("""{"result":"r1","fileId":"file"}""", server.takeRequest().body?.utf8())
    }

    @Test
    fun `a newer server's re-sync state reads as unknown`() = runTest {
        server.enqueue(json("""{"state":"queued"}"""))
        assertEquals(SubtitleSyncState.Unknown, FlexarrClient(server.url("/")).subtitleSync("item", "file").state)
    }
}
