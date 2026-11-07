package io.github.flexarr.player

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** When a player holds the screen on. */
class ScreenAwakeTest {
    @Test fun staysOnWhilePlaying() {
        assertTrue(screenStaysOn(playing = true, loading = false, failed = false))
    }

    @Test fun staysOnWhileTheSpinnerIsUp() {
        // Starting a title, or a stall part-way: nothing is playing yet, but it's about to.
        assertTrue(screenStaysOn(playing = false, loading = true, failed = false))
    }

    @Test fun letsGoOverAPauseOrTheEnd() {
        assertFalse(screenStaysOn(playing = false, loading = false, failed = false))
    }

    @Test fun staysOnWhileTheNextEpisodeIsCountedDownToOverTheLastFrame() {
        // The file has ended and nothing plays, but the clock is counting down to the next one.
        assertTrue(screenStaysOn(playing = false, loading = false, failed = false, countingDown = true, ended = true))
    }

    @Test fun letsGoOverACountdownHeldByAPause() {
        // Paused over the credits or an intro: the countdown follows the picture, so it waits as long as the pause does.
        assertFalse(screenStaysOn(playing = false, loading = false, failed = false, countingDown = true, ended = false))
        // The last frame with nothing counting down (Next Episode held, or no next one).
        assertFalse(screenStaysOn(playing = false, loading = false, failed = false, countingDown = false, ended = true))
    }

    @Test fun letsGoOverAnError() {
        // A title that never started still counts as loading (the player was never prepared): the error says otherwise.
        assertFalse(screenStaysOn(playing = false, loading = true, failed = true))
        assertFalse(screenStaysOn(playing = false, loading = false, failed = true))
    }
}
