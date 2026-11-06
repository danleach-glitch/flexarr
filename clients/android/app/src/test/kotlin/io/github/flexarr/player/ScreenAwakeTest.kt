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

    @Test fun letsGoOverAnError() {
        // A title that never started still counts as loading (the player was never prepared): the error says otherwise.
        assertFalse(screenStaysOn(playing = false, loading = true, failed = true))
        assertFalse(screenStaysOn(playing = false, loading = false, failed = true))
    }
}
