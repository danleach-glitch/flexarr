package io.github.flexarr.audio

import io.github.flexarr.core.Artwork
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.UserData
import org.junit.Assert.assertEquals
import org.junit.Assert.assertSame
import org.junit.Test

class MusicQueueTest {
    private fun track(id: String) =
        ItemSummary(id = id, libraryId = "music", kind = MediaKind.Track, title = id, childCount = 0, artwork = Artwork(), userData = UserData(0.0, false, 0))

    private fun ids(list: List<ItemSummary>) = list.joinToString(",") { it.id }

    private val abc = listOf(track("a"), track("b"), track("c"))

    @Test fun playNextSlotsInAfterTheCurrentTrack() {
        val q = MusicQueue(abc, abc, index = 1, shuffled = false).playNext(listOf(track("x"), track("y")))
        assertEquals("a,b,x,y,c", ids(q.tracks))
        assertEquals("a,b,x,y,c", ids(q.original))
        assertEquals(1, q.index)
    }

    @Test fun enqueueAppends() {
        val q = MusicQueue(abc, abc, index = 0, shuffled = false).enqueue(listOf(track("x")))
        assertEquals("a,b,c,x", ids(q.tracks))
        assertEquals("a,b,c,x", ids(q.original))
        assertEquals(0, q.index)
    }

    @Test fun shuffledTheGivenOrderGetsThemToo() {
        // Playing b, shuffled to b,c,a; given order a,b,c.
        val shuffled = listOf(track("b"), track("c"), track("a"))
        val next = MusicQueue(shuffled, abc, index = 0, shuffled = true).playNext(listOf(track("x")))
        assertEquals("b,x,c,a", ids(next.tracks))
        assertEquals("a,b,x,c", ids(next.original))
        val end = MusicQueue(shuffled, abc, index = 0, shuffled = true).enqueue(listOf(track("y")))
        assertEquals("b,c,a,y", ids(end.tracks))
        assertEquals("a,b,c,y", ids(end.original))
    }

    @Test fun nothingAddedOrNothingQueuedChangesNothing() {
        val q = MusicQueue(abc, abc, index = 0, shuffled = false)
        assertSame(q, q.playNext(emptyList()))
        assertSame(q, q.enqueue(emptyList()))
        val empty = MusicQueue(emptyList(), emptyList(), index = 0, shuffled = false)
        assertSame(empty, empty.enqueue(abc))
    }

    @Test fun theSameTrackCanBeQueuedTwice() {
        val q = MusicQueue(abc, abc, index = 2, shuffled = false).playNext(listOf(track("a")))
        assertEquals("a,b,c,a", ids(q.tracks))
    }
}
