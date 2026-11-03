package io.github.flexarr.audio

import io.github.flexarr.audio.MusicPlayer.RepeatMode
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class GaplessTest {
    @Test fun theNextTrackInTheQueueIsGotReady() {
        assertEquals(2, Gapless.upcoming(index = 1, size = 4, repeat = RepeatMode.Off))
        assertEquals(2, Gapless.upcoming(index = 1, size = 4, repeat = RepeatMode.All))
    }

    @Test fun theLastTrackHasNothingAfterItUnlessRepeatingAll() {
        assertNull(Gapless.upcoming(index = 3, size = 4, repeat = RepeatMode.Off))
        assertEquals(0, Gapless.upcoming(index = 3, size = 4, repeat = RepeatMode.All))
    }

    @Test fun repeatOneAndEmptyQueuesGetNothingReady() {
        assertNull(Gapless.upcoming(index = 1, size = 4, repeat = RepeatMode.One))
        assertNull(Gapless.upcoming(index = 0, size = 0, repeat = RepeatMode.All))
        assertNull(Gapless.upcoming(index = 5, size = 4, repeat = RepeatMode.Off))
    }

    @Test fun itsAskedForInTheLastTwentySeconds() {
        assertFalse(Gapless.isTimeToPrepare(position = 179.0, duration = 200.0))
        assertTrue(Gapless.isTimeToPrepare(position = 180.0, duration = 200.0))
        assertTrue(Gapless.isTimeToPrepare(position = 199.9, duration = 200.0))
        // A short track gets the next one ready from its start.
        assertTrue(Gapless.isTimeToPrepare(position = 0.0, duration = 15.0))
    }

    @Test fun anUnknownLengthNeverAsksEarly() {
        assertFalse(Gapless.isTimeToPrepare(position = 10.0, duration = 0.0))
    }
}
