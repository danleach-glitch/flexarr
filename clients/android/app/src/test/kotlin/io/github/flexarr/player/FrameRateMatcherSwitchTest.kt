package io.github.flexarr.player

import android.app.Activity
import android.app.Application
import android.os.Looper
import android.view.SurfaceView
import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.MimeTypes
import androidx.media3.common.Player
import androidx.media3.common.TrackGroup
import androidx.media3.common.Tracks
import org.junit.Assert.assertEquals
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
    private val player = Proxy.newProxyInstance(Player::class.java.classLoader, arrayOf(Player::class.java)) { _, method, _ ->
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
        return FrameRateMatcher(player, SurfaceView(activity).also(activity::setContentView))
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
}
