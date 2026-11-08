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
import androidx.media3.exoplayer.video.VideoFrameMetadataListener
import io.github.flexarr.logging.FlexarrLog

/**
 * On a TV, asks the screen for the playing video's frame rate (24 for most films), so motion doesn't judder. The TV's own
 * *Match content frame rate* setting decides whether it switches (Android 12 and later), as the Apple TV follows Match
 * Content: so the app asks for a switch even when it isn't seamless, which Media3 on its own never does. MP4 and the
 * server's HLS say the rate up front, so it's asked before the first frame and the screen's blink comes before the
 * picture. MKV doesn't, so there it's measured from the first frames' timestamps, a couple of seconds in.
 * Android 11 switches only when it's seamless; before that there's nothing to ask.
 */
@OptIn(UnstableApi::class)
internal class FrameRateMatcher(private val player: Player, private val view: SurfaceView) :
    Player.Listener, SurfaceHolder.Callback, VideoFrameMetadataListener {
    /** The rate the video wants, and the one last asked of the surface (−1: a new surface, nothing asked yet). */
    private var wanted = 0f
    private var asked = 0f
    private var stopped = false

    /** Frame times (microseconds) while the format hasn't said its rate; touched only on the playback thread. */
    private val times = ArrayDeque<Long>()

    /** Frames looked at since measuring started: a video with no steady rate (a variable frame rate) is given up on. */
    private var looked = 0

    @Volatile private var measuring = false

    fun start() {
        if (Build.VERSION.SDK_INT < 30) return
        stopped = false
        val exo = player as? ExoPlayer
        // This sets the rate itself; Media3's own (seamless only) would undo it.
        exo?.videoChangeFrameRateStrategy = C.VIDEO_CHANGE_FRAME_RATE_STRATEGY_OFF
        exo?.setVideoFrameMetadataListener(this)
        player.addListener(this)
        view.holder.addCallback(this)
        onTracksChanged(player.currentTracks)
    }

    fun stop() {
        if (Build.VERSION.SDK_INT < 30) return
        stopped = true
        measuring = false
        (player as? ExoPlayer)?.clearVideoFrameMetadataListener(this)
        player.removeListener(this)
        view.holder.removeCallback(this)
        // Back to the screen's own rate for the menus.
        apply(0f)
    }

    override fun onTracksChanged(tracks: Tracks) {
        val rate = frameRate(tracks)
        val video = tracks.groups.any { it.type == C.TRACK_TYPE_VIDEO && it.isSelected }
        measuring = rate == 0f && video
        if (!measuring) apply(rate)
    }

    override fun onVideoFrameAboutToBeRendered(presentationTimeUs: Long, releaseTimeNs: Long, format: Format, mediaFormat: android.media.MediaFormat?) {
        if (!measuring) {
            times.clear()
            looked = 0
            return
        }
        if (times.lastOrNull()?.let { presentationTimeUs <= it } == true) times.clear() // a seek
        times.addLast(presentationTimeUs)
        if (times.size > FRAMES) times.removeFirst()
        val rate = measured(times)
        if (rate == null) {
            // No steady rate after a few windows of frames: the screen is left at its own.
            if (++looked >= FRAMES * GIVE_UP_WINDOWS) {
                measuring = false
                times.clear()
                looked = 0
            }
            return
        }
        measuring = false
        times.clear()
        looked = 0
        view.post { if (!stopped) apply(rate) }
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        asked = -1f
        ask()
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) = Unit
    override fun surfaceDestroyed(holder: SurfaceHolder) = Unit

    private fun apply(rate: Float) {
        wanted = rate
        ask()
    }

    /** Asks the surface for [wanted], once it's there: a rate known before the picture's surface waits for it. */
    private fun ask() {
        val rate = wanted
        if (rate == asked) return
        val surface = view.holder.surface?.takeIf { it.isValid } ?: return
        asked = rate
        if (rate > 0) log.info("Asked the screen for ${DeviceCapabilities.rate(rate)} Hz")
        runCatching {
            when {
                Build.VERSION.SDK_INT >= 31 -> surface.setFrameRate(rate, Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE, Surface.CHANGE_FRAME_RATE_ALWAYS)
                Build.VERSION.SDK_INT >= 30 -> surface.setFrameRate(rate, Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE)
            }
        }
    }

    companion object {
        private val log = FlexarrLog("player")

        /**
         * Frames measured before deciding: MKV times are whole milliseconds, so telling 23.976 from 24 takes a couple
         * of seconds of them (2.5 at 24 fps).
         */
        const val FRAMES = 60

        /** Windows of [FRAMES] measured without finding a steady rate before giving up. */
        private const val GIVE_UP_WINDOWS = 4

        /** The rates videos are made at: a measured one close to one of these is taken as it. */
        private val STANDARD = listOf(23.976f, 24f, 25f, 29.97f, 30f, 47.952f, 48f, 50f, 59.94f, 60f, 100f, 119.88f, 120f)

        /** The selected video track's frame rate, or 0 (the screen's own) when it isn't known. */
        fun frameRate(tracks: Tracks): Float =
            tracks.groups.filter { it.type == C.TRACK_TYPE_VIDEO && it.isSelected }
                .flatMap { group -> (0 until group.length).filter(group::isTrackSelected).map(group::getTrackFormat) }
                .firstNotNullOfOrNull { usable(it) } ?: 0f

        /** A format's frame rate when it's one worth asking for. */
        fun usable(format: Format): Float? = format.frameRate.takeIf { it != Format.NO_VALUE.toFloat() && it in 10f..121f }

        /**
         * The frame rate [times] (presentation times in microseconds, in order) show once there are [FRAMES] of them,
         * matched to a standard rate. A dropped frame leaves a gap two frames wide, counted as two. Null while there are
         * too few, or the gaps aren't whole frames (a variable frame rate), when the screen is better left alone.
         */
        fun measured(times: List<Long>): Float? {
            if (times.size < FRAMES) return null
            val gaps = times.zipWithNext { a, b -> (b - a).toDouble() }
            val median = gaps.sorted()[gaps.size / 2].takeIf { it > 0 } ?: return null
            val steady = gaps.filter { kotlin.math.abs(it - median) <= median * 0.1 }
            if (steady.size < gaps.size / 2) return null
            val frame = steady.average()
            val slots = gaps.map { gap -> Math.round(gap / frame).takeIf { it >= 1 && kotlin.math.abs(gap - it * frame) <= frame * 0.1 } ?: return null }
            val rate = (slots.sum() * 1_000_000.0 / (times.last() - times.first())).toFloat()
            return STANDARD.minBy { kotlin.math.abs(it - rate) }.takeIf { kotlin.math.abs(it - rate) / it < 0.01f }
        }
    }
}
