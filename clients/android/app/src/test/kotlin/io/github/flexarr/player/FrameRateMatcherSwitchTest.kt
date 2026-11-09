package io.github.flexarr.player

import android.app.Activity
import android.app.Application
import android.hardware.display.DisplayManager
import android.os.Looper
import android.view.Display
import android.view.Surface
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
import org.robolectric.android.internal.DisplayConfig
import org.robolectric.annotation.Config
import org.robolectric.annotation.Implementation
import org.robolectric.annotation.Implements
import org.robolectric.shadows.ShadowDisplayManager
import org.robolectric.util.ReflectionHelpers
import java.lang.reflect.Proxy

/** What the matcher wants of the screen as titles come and go. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [34], shadows = [MatchContentSetting::class])
class FrameRateMatcherSwitchTest {
    private var playWhenReady = true
    private var plays = 0
    private val switching = mutableListOf<Boolean>()
    private val leftPaused = mutableListOf<Boolean>()
    private var stayPaused: () -> Unit = {}
    private val holds = object : ScreenHolds {
        override fun holdStarted(stayPaused: () -> Unit) {
            switching += true
            this@FrameRateMatcherSwitchTest.stayPaused = stayPaused
        }

        override fun holdEnded(leftPaused: Boolean) {
            switching += false
            this@FrameRateMatcherSwitchTest.leftPaused += leftPaused
        }
    }

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
        return FrameRateMatcher(player, SurfaceView(activity).also(activity::setContentView), holds)
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

    /** A surface that's there: Robolectric's SurfaceView has none. */
    private class ShownSurfaceView(context: android.content.Context) : SurfaceView(context) {
        val surface = object : Surface(android.graphics.SurfaceTexture(0)) {
            override fun isValid() = true
        }
        private val fake = Proxy.newProxyInstance(SurfaceHolder::class.java.classLoader, arrayOf(SurfaceHolder::class.java)) { _, method, _ ->
            if (method.name == "getSurface") surface else null
        } as SurfaceHolder

        override fun getHolder(): SurfaceHolder = fake
    }

    private fun changeDisplay(change: (DisplayConfig) -> Unit) = ReflectionHelpers.callStaticMethod<Unit>(
        ShadowDisplayManager::class.java,
        "changeDisplay",
        ReflectionHelpers.ClassParameter.from(Int::class.javaPrimitiveType, Display.DEFAULT_DISPLAY),
        ReflectionHelpers.ClassParameter.from(java.util.function.Consumer::class.java, java.util.function.Consumer<DisplayConfig> { change(it) }),
    )

    /** A real matcher on a TV set to always switch, at [current] Hz with 24 and 60 to go to. */
    private fun onATv(current: Int = 60, setting: Int = DisplayManager.MATCH_CONTENT_FRAMERATE_ALWAYS): FrameRateMatcher {
        val activity = Robolectric.buildActivity(Activity::class.java).setup().get()
        fun mode(id: Int, hz: Float) = ReflectionHelpers.callConstructor(
            Display.Mode::class.java,
            ReflectionHelpers.ClassParameter.from(Int::class.javaPrimitiveType, id),
            ReflectionHelpers.ClassParameter.from(Int::class.javaPrimitiveType, 3840),
            ReflectionHelpers.ClassParameter.from(Int::class.javaPrimitiveType, 2160),
            ReflectionHelpers.ClassParameter.from(Float::class.javaPrimitiveType, hz),
        )
        changeDisplay { it.supportedModes = arrayOf(mode(1, 60f), mode(2, 24f)); it.modeId = if (current == 60) 1 else 2; it.defaultModeId = 1 }
        MatchContentSetting.setting = setting
        shadowOf(Looper.getMainLooper()).idle()
        return FrameRateMatcher(player, ShownSurfaceView(activity).also(activity::setContentView), holds)
    }

    private fun theScreenChangesTo(mode: Int) {
        changeDisplay { it.modeId = mode }
        shadowOf(Looper.getMainLooper()).idle()
    }

    @Test fun aTitleAtARateTheScreenMustBlankFor_isHeldUntilItHasSwitched() {
        val matcher = onATv()
        matcher.onTracksChanged(tracks(24f))
        assertEquals(listOf(true), switching)
        assertFalse(playWhenReady)
        // The real listener hears the mode change; the TV then takes a moment to show a picture.
        theScreenChangesTo(2)
        pass(FrameRateMatcher.SETTLE_MS - 1)
        assertEquals(0, plays)
        pass(1)
        assertEquals(1, plays)
        assertEquals(listOf(true, false), switching)
        assertEquals(listOf(false), leftPaused)
    }

    @Test fun aTitleTheScreenNeedsNoChangeFor_isntHeld() {
        onATv(current = 24).onTracksChanged(tracks(24f))
        assertTrue(switching.isEmpty())
        assertTrue(playWhenReady)
        // 30 at 60 is a multiple.
        onATv().onTracksChanged(tracks(30f))
        assertTrue(switching.isEmpty())
    }

    @Test fun aTvNotSetToAlwaysSwitch_isntHeld() {
        onATv(setting = DisplayManager.MATCH_CONTENT_FRAMERATE_SEAMLESSS_ONLY).onTracksChanged(tracks(24f))
        assertTrue(switching.isEmpty())
        assertTrue(playWhenReady)
    }

    @Test fun aPauseMeantAsOne_staysPaused() {
        val matcher = onATv()
        matcher.onTracksChanged(tracks(24f))
        stayPaused()
        assertEquals(listOf(true), leftPaused)
        theScreenChangesTo(2)
        pass(FrameRateMatcher.SWITCH_WAIT_MS * 2)
        assertEquals(0, plays)
        assertFalse(playWhenReady)
        // Play from there just plays, with no hold left to get in its way.
        player.play()
        matcher.onPlayWhenReadyChanged(true, Player.PLAY_WHEN_READY_CHANGE_REASON_USER_REQUEST)
        assertEquals(listOf(true, false), switching)
    }

    @Test fun everyWayOutOfAHold_saysWhetherItsLeftPaused() {
        // Waited out, or switched: plays.
        onATv().onTracksChanged(tracks(24f))
        pass(FrameRateMatcher.SWITCH_WAIT_MS)
        assertEquals(listOf(false), leftPaused)
        leftPaused.clear()
        // Play pressed: playing, whatever the screen's doing.
        val pressed = onATv()
        pressed.onTracksChanged(tracks(25f))
        pressed.onTracksChanged(tracks(24f))
        player.play()
        pressed.onPlayWhenReadyChanged(true, Player.PLAY_WHEN_READY_CHANGE_REASON_USER_REQUEST)
        assertEquals(listOf(false), leftPaused)
        leftPaused.clear()
        // The surface gone (Home): paused, and nobody's been told yet.
        val home = onATv()
        home.onTracksChanged(tracks(24f))
        home.surfaceDestroyed(mockHolder)
        assertEquals(listOf(true), leftPaused)
        leftPaused.clear()
        playWhenReady = true
        // Closed.
        val closed = onATv()
        closed.onTracksChanged(tracks(24f))
        closed.stop()
        assertEquals(listOf(true), leftPaused)
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

    @Test fun aNearRate_stillBlanksWhenTheScreenHasTheExactOne() {
        val tv = listOf(23.976f, 24f, 50f, 59.94f, 60f)
        assertTrue(FrameRateMatcher.blanks(23.976f, 24f, emptyList(), tv))
        assertTrue(FrameRateMatcher.blanks(24f, 23.976f, emptyList(), tv))
        assertTrue(FrameRateMatcher.blanks(59.94f, 60f, emptyList(), tv))
        assertTrue(FrameRateMatcher.blanks(60f, 59.94f, emptyList(), tv))
        assertTrue(FrameRateMatcher.blanks(29.97f, 60f, emptyList(), tv))
        assertTrue(FrameRateMatcher.blanks(23.976f, 120f, emptyList(), tv + 119.88f + 120f))
        // Exact already, a display's rounding included, or at an exact multiple.
        assertFalse(FrameRateMatcher.blanks(59.94f, 59.94006f, emptyList(), tv))
        assertFalse(FrameRateMatcher.blanks(23.976f, 119.88f, emptyList(), tv + 119.88f))
        // It can go to the exact one without blanking.
        assertFalse(FrameRateMatcher.blanks(59.94f, 60f, listOf(59.94f), tv))
        // No closer mode: the near one it's at is as good as it gets, and one it isn't at is still a change.
        assertFalse(FrameRateMatcher.blanks(23.976f, 24f, emptyList(), listOf(24f, 60f)))
        assertTrue(FrameRateMatcher.blanks(23.976f, 60f, emptyList(), listOf(24f, 60f)))
    }

    private val mockHolder = Proxy.newProxyInstance(SurfaceHolder::class.java.classLoader, arrayOf(SurfaceHolder::class.java)) { _, _, _ -> null } as SurfaceHolder
}

/** The TV's own Match content frame rate setting, which Robolectric has no way to set. */
@Implements(DisplayManager::class)
class MatchContentSetting : ShadowDisplayManager() {
    @Implementation(minSdk = 31)
    protected fun getMatchContentFrameRateUserPreference(): Int = setting

    companion object {
        var setting = DisplayManager.MATCH_CONTENT_FRAMERATE_NEVER
    }
}
