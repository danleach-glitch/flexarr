package io.github.flexarr.core

import kotlinx.coroutines.test.runTest
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.serializer
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.time.Instant

class DownloadsTest {
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

    private fun text(name: String) = javaClass.classLoader!!.getResource("$name.json")!!.readText()

    private val ready: OfflineDownload by lazy { FlexarrClient.json.decodeFromString(serializer<OfflineDownload>(), text("offline-track")) }

    private fun local(
        kind: MediaKind = MediaKind.Episode,
        phase: LocalPhase = LocalPhase.Downloaded,
        automatic: Boolean = false,
        finishedAt: Long? = null,
        unsent: UnsentProgress? = null,
        nextId: String? = null,
        itemId: String = "ep1",
    ) = LocalDownload(
        id = "d-$itemId",
        profileId = "p",
        quality = DownloadQuality.Medium,
        item = ready.item.copy(id = itemId, kind = kind),
        phase = phase,
        fileName = if (phase == LocalPhase.Downloaded) "d-$itemId.mp4" else null,
        automatic = automatic,
        finishedAtMillis = finishedAt,
        unsent = unsent,
        nextId = nextId,
    )

    @Test
    fun `reads the server's download, its file name giving the extension to keep`() {
        assertEquals(DownloadStatus.Ready, ready.status)
        assertEquals(31_457_280L, ready.sizeBytes)
        val kept = local().copy(remoteFileName = ready.fileName)
        assertEquals(ready.fileName?.substringAfterLast('.'), kept.fileExtension)
        assertEquals("mp4", local().fileExtension)
    }

    @Test
    fun `a newer server's status and size read as unknown`() {
        val newer = FlexarrClient.json.decodeFromString(
            serializer<OfflineDownload>(),
            text("offline-track").replace("\"Ready\"", "\"Paused\"").replace("\"Original\"", "\"Tiny\""),
        )
        assertEquals(DownloadStatus.Unknown, newer.status)
        assertEquals(DownloadQuality.Unknown, newer.quality)
        assertEquals(ServerAnswer.None, DownloadRules.answer(local(phase = LocalPhase.Preparing), newer))
    }

    @Test
    fun `what the server says decides what happens next`() {
        val waiting = local(phase = LocalPhase.Preparing)
        assertEquals(ServerAnswer.Fetch, DownloadRules.answer(waiting, ready))
        assertEquals(ServerAnswer.None, DownloadRules.answer(local(), ready))
        assertEquals(ServerAnswer.Preparing(0.4), DownloadRules.answer(waiting, ready.copy(status = DownloadStatus.Preparing, progress = 0.4)))
        assertEquals(ServerAnswer.Forget, DownloadRules.answer(waiting, ready.copy(status = DownloadStatus.Removed)))
        assertTrue(DownloadRules.answer(waiting, ready.copy(status = DownloadStatus.Expired)) is ServerAnswer.Failed)
        assertEquals(ServerAnswer.Failed("No room"), DownloadRules.answer(waiting, ready.copy(status = DownloadStatus.Failed, error = "No room")))
        // Here already: the server having let its copy go changes nothing.
        assertEquals(ServerAnswer.None, DownloadRules.answer(local(), ready.copy(status = DownloadStatus.Received)))
    }

    @Test
    fun `only downloads the phone starts by itself wait for Wi-Fi`() {
        assertTrue(DownloadRules.isWiFiOnly(local(automatic = true), automaticOnWiFiOnly = true))
        assertEquals(false, DownloadRules.isWiFiOnly(local(automatic = true), automaticOnWiFiOnly = false))
        assertEquals(false, DownloadRules.isWiFiOnly(local(automatic = false), automaticOnWiFiOnly = true))
    }

    @Test
    fun `episodes go a day after they're finished, films stay, and unsent progress keeps one`() {
        val now = 10 * DownloadRules.DAY_MILLIS
        val old = now - DownloadRules.DAY_MILLIS - 1
        val gone = DownloadRules.finishedWith(
            listOf(
                local(itemId = "a", finishedAt = old),
                local(itemId = "b", finishedAt = now - 1000),
                local(itemId = "c", kind = MediaKind.Movie, finishedAt = old),
                local(itemId = "d", finishedAt = old, unsent = UnsentProgress(10.0, true, old)),
                local(itemId = "e", kind = MediaKind.PodcastEpisode, finishedAt = old),
            ),
            now,
        )
        assertEquals(listOf("a", "e"), gone.map { it.item.id })
    }

    @Test
    fun `an Original the phone can't play comes at High`() {
        val options = listOf(DownloadOption(DownloadQuality.Original, 9, available = false), DownloadOption(DownloadQuality.High, 5, true))
        assertEquals(DownloadQuality.High, DownloadRules.quality(DownloadQuality.Original, options))
        assertEquals(DownloadQuality.Original, DownloadRules.quality(DownloadQuality.Original, null))
        assertEquals(DownloadQuality.Small, DownloadRules.quality(DownloadQuality.Small, options))
    }

    @Test
    fun `the next episode is fetched once, for a downloaded episode`() {
        val episode = local(nextId = "ep2")
        assertEquals("ep2", DownloadRules.nextToFetch(episode, listOf(episode)))
        assertNull(DownloadRules.nextToFetch(episode, listOf(episode, local(itemId = "ep2", phase = LocalPhase.Preparing))))
        assertNull(DownloadRules.nextToFetch(local(kind = MediaKind.Movie, nextId = "x"), emptyList()))
        assertNull(DownloadRules.nextToFetch(local(phase = LocalPhase.Preparing, nextId = "ep2"), emptyList()))
    }

    @Test
    fun `watching offline keeps the place, and finishing starts it over`() {
        val watched = local().recorded(600.0, finished = false, now = 5)
        assertEquals(600.0, watched.startPosition, 0.0)
        assertEquals(UnsentProgress(600.0, false, 5), watched.unsent)
        val finished = watched.recorded(2_000.0, finished = true, now = 9)
        assertEquals(9L, finished.finishedAtMillis)
        assertEquals(0.0, finished.startPosition, 0.0)
        assertTrue(finished.unsent!!.finished)
        // The first half minute of a film isn't a place to come back to; any of a podcast is.
        assertEquals(0.0, local().recorded(20.0, false, 1).startPosition, 0.0)
        assertEquals(20.0, local(kind = MediaKind.PodcastEpisode).recorded(20.0, false, 1).startPosition, 0.0)
    }

    @Test
    fun `a saved list reads back`() {
        val list = listOf(local(), local(itemId = "x", phase = LocalPhase.Failed).copy(failure = "Nope"))
        val serializer = ListSerializer(LocalDownload.serializer())
        assertEquals(list, FlexarrClient.json.decodeFromString(serializer, FlexarrClient.json.encodeToString(serializer, list)))
    }

    @Test
    fun `asks for a download, follows it, fetches its file and says what was watched`() = runTest {
        val client = FlexarrClient(server.url("/"), apiKey = "key")
        server.enqueue(json(text("offline-track")))
        server.enqueue(MockResponse.Builder().code(204).build())

        val requested = client.requestDownload("item", DownloadQuality.Medium)
        assertEquals(ready.id, requested.id)
        val request = server.takeRequest()
        assertEquals("/api/v1/offline", request.target)
        assertEquals("""{"itemId":"item","quality":"Medium"}""", request.body?.utf8())

        client.reportOfflineProgress("item", 61.5, finished = false, playedAt = Instant.parse("2026-10-29T10:00:00Z"))
        val progress = server.takeRequest()
        assertEquals("/api/v1/items/item/offline-progress", progress.target)
        assertEquals("""{"positionSeconds":61.5,"finished":false,"playedAt":"2026-10-29T10:00:00Z"}""", progress.body?.utf8())

        val file = client.downloadFileRequest("abc", fromByte = 1024)
        assertEquals("/api/v1/offline/abc/file", file.url.encodedPath)
        assertEquals("bytes=1024-", file.header("Range"))
        assertEquals("key", file.header("X-Api-Key"))
        assertNull(client.downloadFileRequest("abc").header("Range"))
    }
}
