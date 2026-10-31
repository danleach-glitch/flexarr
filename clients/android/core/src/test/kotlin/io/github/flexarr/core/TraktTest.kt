package io.github.flexarr.core

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
import java.time.Instant
import java.util.Locale

class TraktTest {
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

    private fun json(body: String, code: Int = 200) =
        MockResponse.Builder().code(code).addHeader("Content-Type", "application/json").body(body).build()

    @Test
    fun `reads a connected status`() = runTest {
        server.enqueue(json(
            """{"available":true,"state":"connected","username":"dan","connectedAt":"2026-10-30T10:00:00Z","lastSyncAt":"2026-10-31T09:00:00",
               "lastError":null,"watchlistFull":true,"importedAt":null,"importing":false,"queued":3,"pending":null}""",
        ))
        val status = client().traktStatus()!!
        assertEquals("/api/v1/trakt/status", server.takeRequest().url.encodedPath)
        assertTrue(status.available)
        assertTrue(status.isConnected)
        assertEquals("dan", status.settingsValue)
        assertEquals(3, status.queued)
        assertNull(status.importedAt)
        assertFalse(status.isBusy)
        assertEquals(Instant.parse("2026-10-31T09:00:00Z"), status.lastSync)
        assertEquals("Trakt’s watchlist is full. New titles stay on Flexarr’s list.", status.syncNote)
        assertEquals("Broke", status.copy(lastError = "Broke").syncNote)
    }

    @Test
    fun `reads a waiting code`() {
        val status = FlexarrClient.json.decodeFromString(
            TraktStatus.serializer(),
            """{"available":true,"state":"disconnected","watchlistFull":false,"importing":false,"queued":0,
               "pending":{"state":"Waiting","userCode":"AB12CD34","verificationUrl":"https://trakt.tv/activate","qrUrl":"https://trakt.tv/activate","expiresAt":"2026-10-31T10:10:00Z","error":null}}""",
        )
        assertEquals("AB12CD34", status.waitingCode?.userCode)
        assertEquals("trakt.tv/activate", status.waitingCode?.shortVerificationUrl)
        assertTrue(status.isBusy)
        assertEquals("Off", status.settingsValue)
        assertEquals("Connect Trakt", status.connectLabel)
    }

    @Test
    fun `states from a newer server read as unknown`() {
        val status = FlexarrClient.json.decodeFromString(
            TraktStatus.serializer(),
            """{"available":true,"state":"paused","watchlistFull":false,"importing":false,"queued":0,"somethingNew":1,
               "pending":{"state":"Throttled","userCode":"X","verificationUrl":"u","qrUrl":"u","expiresAt":"2026-10-31T10:10:00Z"}}""",
        )
        assertEquals(TraktAccountState.Unknown, status.state)
        assertEquals(TraktConnectState.Unknown, status.pending?.state)
        assertNull(status.waitingCode)
        assertFalse(status.isConnected)
        assertEquals("That didn’t work. Try again.", status.connectNote)
    }

    @Test
    fun `says why a connect ended, and when Trakt signed out`() {
        val ended = TraktStatus(available = true, state = TraktAccountState.Disconnected, pending = TraktPendingConnect(state = TraktConnectState.InUse))
        assertEquals("That Trakt account is connected to another profile.", ended.connectNote)
        assertEquals("Each profile connects its own account.", ended.copy(pending = null).connectNote)
        assertEquals("The code ran out. Try again.", TraktConnectState.Expired.problem)
        assertNull(TraktConnectState.Waiting.problem)
        val signedOut = TraktStatus(available = true, state = TraktAccountState.NeedsSignIn, username = "dan")
        assertEquals("Connect dan again", signedOut.connectLabel)
        assertEquals("Trakt signed Flexarr out.", signedOut.connectNote)
        assertEquals("Signed out", signedOut.settingsValue)
    }

    @Test
    fun `a server without Trakt, or a kids profile, has none`() = runTest {
        server.enqueue(json("""{"status":404}""", code = 404))
        assertNull(client().traktStatus())
        server.enqueue(json("""{"status":403}""", code = 403))
        assertNull(client().traktStatus())
        server.enqueue(MockResponse.Builder().code(200).addHeader("Content-Type", "text/html").body("<!doctype html><html></html>").build())
        assertNull(client().traktStatus())
    }

    @Test
    fun `the import preview in words`() {
        val preview = TraktImportPreview(traktFilms = 12, traktEpisodes = 1340, inLibrary = 900, newToFlexarr = 210, notOnServer = 40, unmatched = 2, toTrakt = 1)
        assertEquals("Trakt has 12 films and 1,340 episodes", preview.summary(Locale.UK))
        assertEquals(
            "900 are here, 210 not yet watched in Flexarr. 40 will show as watched if they arrive. 1 play from Flexarr goes to Trakt.",
            preview.details(Locale.UK),
        )
        val small = TraktImportPreview(traktFilms = 1, traktEpisodes = 0, inLibrary = 1, newToFlexarr = 0, toTrakt = 3)
        assertEquals("Trakt has 1 film and 0 episodes", small.summary(Locale.UK))
        assertEquals("1 are here, 0 not yet watched in Flexarr. 3 plays from Flexarr go to Trakt.", small.details(Locale.UK))
    }

    @Test
    fun `connects, previews and confirms the import, and syncs`() = runTest {
        server.enqueue(json("""{"state":"Waiting","userCode":"AB12","verificationUrl":"https://trakt.tv/activate","qrUrl":"https://trakt.tv/activate/AB12","expiresAt":"2026-10-31T10:10:00Z"}"""))
        assertEquals("https://trakt.tv/activate/AB12", client().connectTrakt().qrUrl)
        assertEquals("POST", server.takeRequest().method)

        server.enqueue(json("""{"traktFilms":2,"traktEpisodes":3,"inLibrary":1,"newToFlexarr":1,"notOnServer":0,"unmatched":0,"toTrakt":0}"""))
        assertEquals(2, client().previewTraktImport().traktFilms)
        val preview = server.takeRequest()
        assertEquals("/api/v1/trakt/import", preview.url.encodedPath)
        assertNull(preview.url.queryParameter("confirm"))

        server.enqueue(MockResponse.Builder().code(202).build())
        client().confirmTraktImport()
        assertEquals("true", server.takeRequest().url.queryParameter("confirm"))

        server.enqueue(MockResponse.Builder().code(202).build())
        client().syncTraktNow()
        assertEquals("/api/v1/trakt/sync", server.takeRequest().url.encodedPath)

        server.enqueue(MockResponse.Builder().code(204).build())
        client().cancelTraktConnect()
        server.takeRequest().let {
            assertEquals("DELETE", it.method)
            assertEquals("/api/v1/trakt/connect", it.url.encodedPath)
        }

        server.enqueue(MockResponse.Builder().code(204).build())
        client().disconnectTrakt()
        server.takeRequest().let {
            assertEquals("DELETE", it.method)
            assertEquals("/api/v1/trakt", it.url.encodedPath)
        }
    }

    @Test
    fun `reads the lists and saves the choice`() = runTest {
        server.enqueue(json(
            """[{"id":"dan/1","name":"Mine","owner":null,"itemCount":24,"liked":false,"selected":true},
                {"id":"someone/2","name":"Theirs","owner":"Someone","itemCount":1,"liked":true,"selected":false}]""",
        ))
        val lists = client().traktLists()
        assertEquals(listOf("dan/1", "someone/2"), lists.map { it.id })
        assertEquals("24 titles", lists[0].detail)
        assertEquals("1 title · by Someone", lists[1].detail)
        server.takeRequest()

        server.enqueue(MockResponse.Builder().code(204).build())
        client().saveTraktLists(listOf("dan/1", "someone/2"))
        val put = server.takeRequest()
        assertEquals("PUT", put.method)
        assertEquals("/api/v1/trakt/lists", put.url.encodedPath)
        assertEquals("""{"ids":["dan/1","someone/2"]}""", put.body?.utf8())
    }

    @Test
    fun `says how long ago`() {
        val now = Instant.parse("2026-10-31T12:00:00Z")
        assertEquals("Just now", relativeTime(now.minusSeconds(20), now))
        assertEquals("1 minute ago", relativeTime(now.minusSeconds(60), now))
        assertEquals("5 minutes ago", relativeTime(now.minusSeconds(300), now))
        assertEquals("3 hours ago", relativeTime(now.minusSeconds(3 * 3600), now))
        assertEquals("Yesterday", relativeTime(now.minusSeconds(30 * 3600), now))
        assertEquals("4 days ago", relativeTime(now.minusSeconds(4 * 86400), now))
    }
}
