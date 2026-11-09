package io.github.flexarr.player

import android.hardware.display.DisplayManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.view.Display
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
 *
 * A switch that isn't seamless blacks the screen for a few seconds, so playing is held across it ([hold]), or those
 * seconds of the film and its sound would be lost. [onSwitching] says while it is, for the player to show it's waiting.
 */
@OptIn(UnstableApi::class)
internal class FrameRateMatcher(
    private val player: Player,
    private val view: SurfaceView,
    private val onSwitching: (Boolean) -> Unit = {},
) : Player.Listener, SurfaceHolder.Callback, VideoFrameMetadataListener {
    /** The rate the video wants, and the one last asked of the surface (−1: a new surface, nothing asked yet). */
    internal var wanted = 0f
        private set
    private var asked = 0f
    private var stopped = false

    /** Frame times (microseconds) while the format hasn't said its rate; touched only on the playback thread. */
    private val times = ArrayDeque<Long>()

    /** Frames looked at since measuring started: a video with no steady rate (a variable frame rate) is given up on. */
    private var looked = 0

    @Volatile private var measuring = false

    private var holding = false
    private var heldMode: Int? = null
    private val endHold = Runnable { release(resume = true) }
    private val displays get() = view.context.getSystemService(DisplayManager::class.java)
    private val displayListener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) = Unit
        override fun onDisplayRemoved(displayId: Int) = Unit
        override fun onDisplayChanged(displayId: Int) {
            if (displayId == Display.DEFAULT_DISPLAY && displays.getDisplay(displayId)?.mode?.modeId != heldMode) screenSwitched()
        }
    }

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
        release(resume = false)
        (player as? ExoPlayer)?.clearVideoFrameMetadataListener(this)
        player.removeListener(this)
        view.holder.removeCallback(this)
        // Back to the screen's own rate for the menus.
        apply(0f)
    }

    override fun onTracksChanged(tracks: Tracks) {
        // Between titles (each setMediaItem empties them): the screen stays as it is, or it blinks twice for a film at
        // the same rate as the last. Only stop() goes back to its own.
        if (tracks.isEmpty) {
            measuring = false
            return
        }
        val rate = frameRate(tracks)
        val video = tracks.groups.any { it.type == C.TRACK_TYPE_VIDEO && it.isSelected }
        measuring = rate == 0f && video
        if (!measuring) apply(rate)
    }

    /** Play pressed while held, or the next title started: that wins, over a black screen if need be. */
    override fun onPlayWhenReadyChanged(playWhenReady: Boolean, reason: Int) {
        if (playWhenReady) release(resume = false)
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
            // No steady rate after a few windows of frames: the screen goes back to its own, if the title before asked for another.
            if (++looked >= FRAMES * GIVE_UP_WINDOWS) {
                measuring = false
                times.clear()
                looked = 0
                view.post { if (!stopped) apply(0f) }
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

    /** Gone from the screen: nothing to wait for, and it mustn't start playing behind whatever's there now. */
    override fun surfaceDestroyed(holder: SurfaceHolder) = release(resume = false)

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
        if (rate > 0) {
            log.info("Asked the screen for ${DeviceCapabilities.rate(rate)} Hz")
            if (willBlank(rate)) hold()
        }
        runCatching {
            when {
                Build.VERSION.SDK_INT >= 31 -> surface.setFrameRate(rate, Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE, Surface.CHANGE_FRAME_RATE_ALWAYS)
                Build.VERSION.SDK_INT >= 30 -> surface.setFrameRate(rate, Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE)
            }
        }
    }

    /** Whether asking for [rate] has the screen go black: the TV's setting is Always, and it isn't seamless. */
    private fun willBlank(rate: Float): Boolean {
        if (Build.VERSION.SDK_INT < 31) return false
        if (displays.matchContentFrameRateUserPreference != DisplayManager.MATCH_CONTENT_FRAMERATE_ALWAYS) return false
        val display = view.display ?: return false
        val mode = display.mode
        val sameSize = display.supportedModes.filter { it.physicalWidth == mode.physicalWidth && it.physicalHeight == mode.physicalHeight }
        return blanks(rate, mode.refreshRate, mode.alternativeRefreshRates.asList(), sameSize.map { it.refreshRate })
    }

    /** Holds playing until the screen has changed, or it's been long enough that it won't. Already paused, nothing's lost. */
    internal fun hold() {
        if (holding || !player.playWhenReady) return
        holding = true
        heldMode = view.display?.mode?.modeId
        displays.registerDisplayListener(displayListener, Handler(Looper.getMainLooper()))
        onSwitching(true)
        player.pause()
        view.postDelayed(endHold, SWITCH_WAIT_MS)
    }

    /** The mode changed. The TV takes a moment to show a picture again. */
    internal fun screenSwitched() {
        if (!holding) return
        view.removeCallbacks(endHold)
        view.postDelayed(endHold, SETTLE_MS)
    }

    private fun release(resume: Boolean) {
        if (!holding) return
        holding = false
        view.removeCallbacks(endHold)
        displays.unregisterDisplayListener(displayListener)
        onSwitching(false)
        if (resume) player.play()
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

        /** How long a switch is waited for: when it's that late, it isn't coming. */
        const val SWITCH_WAIT_MS = 4_000L

        /** How long after the mode changes playing starts again, for the TV to show a picture. */
        const val SETTLE_MS = 1_000L

        /**
         * Whether asking for [rate] makes a screen at [current] change mode in a way that blanks it: [seamless] are the
         * rates it can go to without ([android.view.Display.Mode.getAlternativeRefreshRates]), [modes] all it has at this
         * size. A rate the screen is already at a multiple of (24 at 120) needs no change, and without a mode that suits
         * there's none to change to. 59.94 and 60, or 23.976 and 24, aren't the same: the screen goes to the exact one
         * if it has it, so the near one only counts when it hasn't.
         */
        fun blanks(rate: Float, current: Float, seamless: List<Float>, modes: List<Float>): Boolean {
            val multiple = { hz: Float -> Math.round(hz / rate).toFloat() }
            fun near(hz: Float) = (hz / rate).let { kotlin.math.abs(it - multiple(hz)) < 0.01f && it >= 0.99f }
            // A thousandth apart is the difference between the two kinds of rate, so half of it is the same rate.
            fun exact(hz: Float) = near(hz) && kotlin.math.abs(hz / rate / multiple(hz) - 1f) < 0.0005f
            val suits: (Float) -> Boolean = if ((modes + current).any(::exact)) ::exact else ::near
            return !suits(current) && seamless.none(suits) && modes.any(suits)
        }

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
