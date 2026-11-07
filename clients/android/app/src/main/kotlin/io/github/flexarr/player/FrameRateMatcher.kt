package io.github.flexarr.player

import android.os.Build
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.annotation.OptIn
import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.Player
import androidx.media3.common.Tracks
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer

/**
 * On a TV, asks the screen for the playing video's frame rate (24 for most films), so motion doesn't judder. The TV's own
 * *Match content frame rate* setting decides whether it switches (Android 12 and later), as the Apple TV follows Match
 * Content: so the app asks for a switch even when it isn't seamless, which Media3 on its own never does. Asked as soon
 * as the tracks are known, before the first frame, so the screen's blink comes before the picture rather than in it.
 * Android 11 switches only when it's seamless; before that there's nothing to ask.
 */
@OptIn(UnstableApi::class)
internal class FrameRateMatcher(private val player: Player, private val view: SurfaceView) : Player.Listener, SurfaceHolder.Callback {
    private var asked = 0f

    fun start() {
        if (Build.VERSION.SDK_INT < 30) return
        // This sets the rate itself; Media3's own (seamless only) would undo it.
        (player as? ExoPlayer)?.videoChangeFrameRateStrategy = C.VIDEO_CHANGE_FRAME_RATE_STRATEGY_OFF
        player.addListener(this)
        view.holder.addCallback(this)
        apply(frameRate(player.currentTracks))
    }

    fun stop() {
        if (Build.VERSION.SDK_INT < 30) return
        player.removeListener(this)
        view.holder.removeCallback(this)
        // Back to the screen's own rate for the menus.
        apply(0f)
    }

    override fun onTracksChanged(tracks: Tracks) = apply(frameRate(tracks))

    override fun surfaceCreated(holder: SurfaceHolder) {
        val rate = asked
        asked = -1f
        apply(rate)
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
    override fun surfaceDestroyed(holder: SurfaceHolder) = Unit

    private fun apply(rate: Float) {
        if (rate == asked) return
        val surface = view.holder.surface?.takeIf { it.isValid } ?: return
        asked = rate
        runCatching {
            when {
                Build.VERSION.SDK_INT >= 31 -> surface.setFrameRate(rate, Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE, Surface.CHANGE_FRAME_RATE_ALWAYS)
                Build.VERSION.SDK_INT >= 30 -> surface.setFrameRate(rate, Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE)
            }
        }
    }

    companion object {
        /** The selected video track's frame rate, or 0 (the screen's own) when it isn't known. */
        fun frameRate(tracks: Tracks): Float =
            tracks.groups.filter { it.type == C.TRACK_TYPE_VIDEO && it.isSelected }
                .flatMap { group -> (0 until group.length).filter(group::isTrackSelected).map(group::getTrackFormat) }
                .firstNotNullOfOrNull { usable(it) } ?: 0f

        /** A format's frame rate when it's one worth asking for. */
        fun usable(format: Format): Float? = format.frameRate.takeIf { it != Format.NO_VALUE.toFloat() && it in 10f..121f }
    }
}
