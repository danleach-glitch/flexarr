package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TransferQueueTest {
    private fun download(
        id: String,
        phase: LocalPhase = LocalPhase.Preparing,
        queued: Boolean = false,
        automatic: Boolean = false,
        profile: String = "p",
        created: Long = 0,
    ) = LocalDownload(
        id = id,
        profileId = profile,
        quality = DownloadQuality.Medium,
        item = ItemSummary(id = id, libraryId = "tv", kind = MediaKind.Episode, title = id, childCount = 0, artwork = Artwork(), userData = UserData(0.0, false, 0)),
        phase = phase,
        fileName = if (phase == LocalPhase.Downloaded) "$id.mp4" else null,
        queued = queued,
        automatic = automatic,
        createdAtMillis = created,
    )

    @Test
    fun `a download waits once the profile has three under way`() {
        val two = listOf(download("a"), download("b", LocalPhase.Downloading))
        assertFalse(TransferQueue.waits("p", two))
        assertTrue(TransferQueue.waits("p", two + download("c")))
    }

    @Test
    fun `downloads here, failed, waiting or fetched by themselves don't take a place`() {
        val list = listOf(
            download("here", LocalPhase.Downloaded),
            download("failed", LocalPhase.Failed),
            download("waiting", queued = true),
            download("next", automatic = true),
            download("running"),
        )
        assertFalse(TransferQueue.waits("p", list))
        assertTrue(TransferQueue.waits("p", list + download("two") + download("three")))
    }

    @Test
    fun `each profile has places of its own`() {
        val list = listOf(download("a", profile = "x"), download("b", profile = "x"), download("c", profile = "x"))
        assertTrue(TransferQueue.waits("x", list))
        assertFalse(TransferQueue.waits("y", list))
    }

    @Test
    fun `the first asked for start as places come free`() {
        val list = listOf(
            download("running"),
            download("third", queued = true, created = 3),
            download("first", queued = true, created = 1),
            download("second", queued = true, created = 2),
        )
        assertEquals(listOf("first", "second"), TransferQueue.toStart(list).map { it.id })
    }

    @Test
    fun `nothing starts while every place is taken`() {
        val list = listOf(download("a"), download("b"), download("c"), download("d", queued = true))
        assertEquals(emptyList<LocalDownload>(), TransferQueue.toStart(list))
    }

    @Test
    fun `places are counted for each profile`() {
        val list = listOf(
            download("a", profile = "x"), download("b", profile = "x"), download("c", profile = "x"),
            download("x-waiting", queued = true, profile = "x"),
            download("y-waiting", queued = true, profile = "y"),
        )
        assertEquals(listOf("y-waiting"), TransferQueue.toStart(list).map { it.id })
    }

    @Test
    fun `a waiting download that failed meanwhile isn't started, nor shown as queued`() {
        val failed = download("f", LocalPhase.Failed, queued = true)
        assertEquals(emptyList<LocalDownload>(), TransferQueue.toStart(listOf(failed)))
        assertFalse(TransferQueue.isQueued(failed))
        assertTrue(TransferQueue.isQueued(download("q", queued = true)))
    }

    @Test
    fun `the queued mark is saved, and a list saved before it reads as not queued`() {
        val json = FlexarrClient.json
        val saved = json.encodeToString(LocalDownload.serializer(), download("q", queued = true))
        assertTrue(json.decodeFromString(LocalDownload.serializer(), saved).queued)
        val older = json.encodeToString(LocalDownload.serializer(), download("o"))
        assertFalse(older.contains("queued"))
        assertFalse(json.decodeFromString(LocalDownload.serializer(), older).queued)
    }
}
