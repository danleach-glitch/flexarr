package io.github.flexarr.player

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Where the page Play was pressed on goes once the player closes. */
class PlaybackEndingTest {
    @Test fun movesOnPastAFinishedEpisode() {
        assertEquals("e3", PlaybackEnding("page", "e2", "e2", watched = true, next = "e3").moveOn("e2"))
    }

    @Test fun goesToTheEpisodeItStoppedPartWayThrough() {
        assertEquals("e4", PlaybackEnding("page", "e2", "e4", watched = false, next = "e5").moveOn("e2"))
    }

    @Test fun staysWhenStoppedPartWayOrAtTheLastEpisode() {
        assertNull(PlaybackEnding(null, "e2", "e2", watched = false, next = "e3").moveOn("e2"))
        assertNull(PlaybackEnding(null, "e9", "e9", watched = true, next = null).moveOn("e9"))
    }

    @Test fun onlyMovesThePageShowingWhatWasStarted() {
        assertNull(PlaybackEnding("page", "e2", "e2", watched = true, next = "e3").moveOn("series"))
    }

    @Test fun watchedAtTheCreditsOr92PercentWhicheverComesFirst() {
        assertTrue(PlaybackEnding.isWatched(94.0, 100.0, 97.0))
        assertTrue(PlaybackEnding.isWatched(85.0, 100.0, 84.0))
        assertFalse(PlaybackEnding.isWatched(90.0, 100.0, null))
        assertFalse(PlaybackEnding.isWatched(0.0, 0.0, null))
    }
}
