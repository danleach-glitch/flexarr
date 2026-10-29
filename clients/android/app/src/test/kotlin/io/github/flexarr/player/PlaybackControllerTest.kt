package io.github.flexarr.player

import io.github.flexarr.core.Marker
import io.github.flexarr.core.MarkerType
import io.github.flexarr.core.PlaybackInfo
import io.github.flexarr.fixture
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** When an episode counts as watched: at its credits, or near the end without any. */
class PlaybackControllerTest {
    /** 2831 seconds long, its credits marked from 2811.72. */
    private val withCredits = fixture<PlaybackInfo>("playback")

    private val withoutCredits = withCredits.copy(markers = withCredits.markers.filter { it.type != MarkerType.Credits }, durationSeconds = 1_000.0)

    @Test
    fun `watched once the credits start`() {
        assertFalse(PlaybackController.isPastCredits(withCredits, 2_811.7))
        assertTrue(PlaybackController.isPastCredits(withCredits, 2_811.72))
        assertTrue(PlaybackController.isPastCredits(withCredits, 2_831.0))
    }

    @Test
    fun `with no credits marked, watched from 92 percent of the way through`() {
        assertFalse(PlaybackController.isPastCredits(withoutCredits, 919.0))
        assertTrue(PlaybackController.isPastCredits(withoutCredits, 920.0))
    }

    @Test
    fun `the intro is no reason to count it watched`() {
        val intro = withCredits.markers.first { it.type == MarkerType.Intro }
        assertFalse(PlaybackController.isPastCredits(withCredits, intro.start))
    }

    @Test
    fun `the very start never counts, whatever the markers say`() {
        val creditsFirst = withCredits.copy(markers = listOf(Marker(MarkerType.Credits, 0.0, 10.0)))
        assertFalse(PlaybackController.isPastCredits(creditsFirst, 0.0))
        assertFalse(PlaybackController.isPastCredits(withoutCredits.copy(durationSeconds = 0.0), 0.0))
        assertTrue(PlaybackController.isPastCredits(creditsFirst, 0.5))
    }
}
