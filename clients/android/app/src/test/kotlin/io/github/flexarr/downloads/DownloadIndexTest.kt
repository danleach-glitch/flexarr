package io.github.flexarr.downloads

import io.github.flexarr.core.CollectionRules
import io.github.flexarr.core.CollectionStatus
import io.github.flexarr.core.DownloadQuality
import io.github.flexarr.core.LocalDownload
import io.github.flexarr.core.LocalPhase
import io.github.flexarr.core.PlaybackInfo
import io.github.flexarr.fixture
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Test

/** Finding a download without going down the list: the answers going down it gave, for a list of any length. */
class DownloadIndexTest {
    private val title = fixture<PlaybackInfo>("playback").item

    private fun download(id: String, itemId: String, profileId: String = "p", phase: LocalPhase = LocalPhase.Downloaded) = LocalDownload(
        id = id,
        profileId = profileId,
        quality = DownloadQuality.Original,
        item = title.copy(id = itemId),
        phase = phase,
        fileName = if (phase == LocalPhase.Downloaded) "$id.mp3" else null,
    )

    /** As the store found them before: the first in the list that matches. */
    private fun List<LocalDownload>.scanned(profileId: String?, itemId: String) = firstOrNull { it.profileId == profileId && it.item.id == itemId }

    @Test
    fun `a download is found by its id, and by whose it is and what it's of`() {
        val mine = download("d1", "track1")
        val theirs = download("d2", "track1", profileId = "q")
        val index = DownloadIndex(listOf(mine, theirs))

        assertSame(mine, index.byId("d1"))
        assertSame(theirs, index.byId("d2"))
        assertSame(mine, index.entry("p", "track1"))
        assertSame(theirs, index.entry("q", "track1"))
    }

    @Test
    fun `what isn't there isn't found`() {
        val index = DownloadIndex(listOf(download("d1", "track1")))

        assertNull(index.byId("d9"))
        assertNull(index.entry("p", "track9"))
        // Another profile's, or nobody signed in.
        assertNull(index.entry("q", "track1"))
        assertNull(index.entry(null, "track1"))
        assertNull(DownloadIndex(emptyList()).entry("p", "track1"))
    }

    @Test
    fun `of two entries for one title or id, the first in the list is found, as it was`() {
        val first = download("d1", "track1", phase = LocalPhase.Failed)
        val second = download("d2", "track1")
        val sameId = download("d1", "track2")
        val index = DownloadIndex(listOf(first, second, sameId))

        assertSame(first, index.entry("p", "track1"))
        assertSame(first, index.byId("d1"))
        assertSame(sameId, index.entry("p", "track2"))
    }

    @Test
    fun `the list comes back as it was given`() {
        val list = listOf(download("d2", "b"), download("d1", "a"))

        assertSame(list, DownloadIndex(list).all)
    }

    @Test
    fun `a big playlist's tracks are each found as going down the list found them`() {
        // A thousand tracks, a third of them another profile's as well, some still coming.
        val list = (0 until 1_000).flatMap { n ->
            listOfNotNull(
                download("d$n", "track$n", phase = if (n % 7 == 0) LocalPhase.Downloading else LocalPhase.Downloaded),
                if (n % 3 == 0) download("other$n", "track$n", profileId = "q") else null,
            )
        }
        val index = DownloadIndex(list)
        // A playlist that also holds tracks never asked for.
        val trackIds = (0 until 1_100).map { "track$it" }

        for (profile in listOf("p", "q", null)) {
            assertEquals(trackIds.map { list.scanned(profile, it) }, trackIds.map { index.entry(profile, it) })
        }
        assertEquals(list.map { it.id }, list.map { index.byId(it.id)?.id })
        // And so the playlist's status is what it was.
        val status = CollectionRules.status(trackIds.take(1_000).map { index.entry("p", it) }, queueing = false)
        assertEquals(CollectionRules.status(trackIds.take(1_000).map { list.scanned("p", it) }, queueing = false), status)
        assertEquals(CollectionStatus.Downloading::class, status::class)
    }
}
