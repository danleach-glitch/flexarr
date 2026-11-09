package io.github.flexarr.player

import android.app.Activity
import android.app.Application
import android.os.Looper
import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.MimeTypes
import androidx.media3.common.Player
import androidx.media3.common.TrackGroup
import androidx.media3.common.Tracks
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import java.lang.reflect.Proxy

/** What the matcher wants of the screen as titles come and go. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [34])
class FrameRateMatcherSwitchTest {
    private var playWhenReady = true
    private var plays = 0
    private val switching = mutableListOf<Boolean>()

    private val player = Proxy.newProxyInstance(Player::class.java.classLoader, arrayOf(Player::class.java)) { _, method, _ ->
        when (method.name) {
            "play" -> { playWhenReady = true; plays++ }
            "pause" -> playWhenReady = false
        }
        if (method.name == "getPlayWhenReady") return@newProxyInstance playWhenReady
        when (method.returnType) {
            Tracks::class.java -> Tracks.EMPTY
            java.lang.Boolean.TYPE -> false
            Integer.TYPE -> 0
            java.lang.Long.TYPE -> 0L
            java.lang.Float.TYPE -> 0f
            else -> null
        }
    } as Player

    /** On a screen, as a view's post only runs once it's attached. */
    private fun matcher(): FrameRateMatcher {
        val activity = Robolectric.buildActivity(Activity::class.java).setup().get()
        return FrameRateMatcher(player, SurfaceView(activity).also(activity::setContentView), switching::add)
    }

    private fun tracks(rate: Float = Format.NO_VALUE.toFloat()): Tracks {
        val format = Format.Builder().setSampleMimeType(MimeTypes.VIDEO_H264).setFrameRate(rate).build()
        return Tracks(listOf(Tracks.Group(TrackGroup(format), false, intArrayOf(C.FORMAT_HANDLED), booleanArrayOf(true))))
    }

    @Test fun betweenTitles_theScreenKeepsItsRate() {
        val matcher = matcher()
        matcher.onTracksChanged(tracks(24f))
        matcher.onTracksChanged(Tracks.EMPTY)
        assertEquals(24f, matcher.wanted)
        matcher.onTracksChanged(tracks(24f))
        assertEquals(24f, matcher.wanted)
    }

    @Test fun aNewTitleAtAnotherRate_asksForIt() {
        val matcher = matcher()
        matcher.onTracksChanged(tracks(24f))
        matcher.onTracksChanged(Tracks.EMPTY)
        matcher.onTracksChanged(tracks(25f))
        assertEquals(25f, matcher.wanted)
    }

    @Test fun stopping_goesBackToTheScreensOwn() {
        val matcher = matcher()
        matcher.start()
        matcher.onTracksChanged(tracks(24f))
        matcher.stop()
        assertEquals(0f, matcher.wanted)
    }

    @Test fun anUnsteadyNextTitle_givesTheScreenBack() {
        val matcher = matcher()
        matcher.onTracksChanged(tracks(24f))
        matcher.onTracksChanged(tracks())
        // Measuring keeps the last title's rate, so a film at the same rate doesn't blink.
        assertEquals(24f, matcher.wanted)
        val format = Format.Builder().build()
        var time = 0L
        repeat(FrameRateMatcher.FRAMES * 5) { frame ->
            time += listOf(11_000L, 17_300L, 23_900L, 31_100L, 41_700L, 53_300L, 67_900L)[frame % 7]
            matcher.onVideoFrameAboutToBeRendered(time, 0, format, null)
        }
        shadowOf(Looper.getMainLooper()).idle()
        assertEquals(0f, matcher.wanted)
    }

    private fun pass(millis: Long) = shadowOf(Looper.getMainLooper()).idleFor(java.time.Duration.ofMillis(millis))

    @Test fun heldForASwitch_playsAgainOnceTheScreenHasChanged() {
        val matcher = matcher()
        matcher.hold()
        assertFalse(playWhenReady)
        assertEquals(listOf(true), switching)
        matcher.screenSwitched()
        pass(FrameRateMatcher.SETTLE_MS - 1)
        assertEquals(0, plays)
        pass(1)
        assertEquals(1, plays)
        assertEquals(listOf(true, false), switching)
    }

    @Test fun aSwitchThatNeverComes_stillPlaysAgain() {
        val matcher = matcher()
        matcher.hold()
        pass(FrameRateMatcher.SWITCH_WAIT_MS - 1)
        assertEquals(0, plays)
        pass(1)
        assertEquals(1, plays)
    }

    @Test fun alreadyPaused_isntStartedAfterASwitch() {
        playWhenReady = false
        val matcher = matcher()
        matcher.hold()
        pass(FrameRateMatcher.SWITCH_WAIT_MS)
        assertEquals(0, plays)
        assertEquals(emptyList<Boolean>(), switching)
    }

    @Test fun playPressedWhileHeld_endsTheHold() {
        val matcher = matcher()
        matcher.hold()
        matcher.onPlayWhenReadyChanged(false, Player.PLAY_WHEN_READY_CHANGE_REASON_USER_REQUEST)
        assertEquals(listOf(true), switching) // its own pause doesn't end it
        matcher.onPlayWhenReadyChanged(true, Player.PLAY_WHEN_READY_CHANGE_REASON_USER_REQUEST)
        assertEquals(listOf(true, false), switching)
        pass(FrameRateMatcher.SWITCH_WAIT_MS)
        assertEquals(0, plays)
    }

    @Test fun leavingWhileHeld_doesntStartPlayingBehindTheScreen() {
        val surface = matcher()
        surface.hold()
        surface.surfaceDestroyed(mockHolder)
        pass(FrameRateMatcher.SWITCH_WAIT_MS)
        assertEquals(0, plays)
        assertEquals(listOf(true, false), switching)

        val stopped = matcher()
        stopped.start()
        playWhenReady = true
        stopped.hold()
        stopped.stop()
        pass(FrameRateMatcher.SWITCH_WAIT_MS)
        assertEquals(0, plays)
    }

    @Test fun onlyASwitchThatIsntSeamless_blanksTheScreen() {
        val tv = listOf(23.976f, 24f, 50f, 59.94f, 60f)
        assertTrue(FrameRateMatcher.blanks(24f, 60f, emptyList(), tv))
        assertTrue(FrameRateMatcher.blanks(23.976f, 59.94f, emptyList(), tv))
        assertTrue(FrameRateMatcher.blanks(25f, 60f, emptyList(), tv))
        // Already there, or at a multiple.
        assertFalse(FrameRateMatcher.blanks(24f, 24f, emptyList(), tv))
        assertFalse(FrameRateMatcher.blanks(24f, 120f, emptyList(), tv + 120f))
        assertFalse(FrameRateMatcher.blanks(30f, 60f, emptyList(), tv))
        // It can go there without blanking.
        assertFalse(FrameRateMatcher.blanks(24f, 60f, listOf(24f), tv))
        // Nothing to go to.
        assertFalse(FrameRateMatcher.blanks(24f, 60f, emptyList(), listOf(50f, 60f)))
    }

    private val mockHolder = Proxy.newProxyInstance(SurfaceHolder::class.java.classLoader, arrayOf(SurfaceHolder::class.java)) { _, _, _ -> null } as SurfaceHolder
}
