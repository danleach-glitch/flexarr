package io.github.flexarr.core

import io.github.flexarr.core.i18n.Words
import io.github.flexarr.core.i18n.t

/**
 * The player's Quality menu (docs/remote-streaming.md): Automatic (the server decides, by the link's speed away from
 * home), Original (the file as it is), or one of the ladder's qualities, asked for as a height and bitrate.
 */
enum class StreamQuality(val raw: String, val height: Int?, val bitrate: Long?) {
    Automatic("automatic", null, null),
    Original("original", null, null),
    P1080("1080p", 1080, 8_000_000),
    P720("720p", 720, 4_000_000),
    P480("480p", 480, 1_500_000);

    /** As the menu says it: a height is the same in every language. */
    val label: String
        get() = when (this) {
            Automatic -> t(Words.automatic)
            Original -> t(Words.originalQuality)
            else -> raw
        }

    /** One of the ladder's qualities (not Automatic or Original). */
    val isLevel: Boolean get() = height != null

    companion object {
        /** The menu, in order. */
        val menu = listOf(Automatic, Original, P1080, P720, P480)

        /** The ladder's qualities, highest first. */
        val levels = listOf(P1080, P720, P480)

        fun of(raw: String?): StreamQuality = entries.firstOrNull { it.raw == raw } ?: Automatic

        /** What a play starts on: the remembered pick away from home, Automatic at home. */
        fun forStart(remembered: StreamQuality, away: Boolean): StreamQuality = if (away) remembered else Automatic

        /** The first quality below a picture [height] pixels high, or null when there's none lower. */
        fun below(height: Int?): StreamQuality? {
            if (height == null || height <= 0) return levels.first()
            return levels.firstOrNull { it.height!! < height }
        }

        /** The quality above [level], or null for no limit (back to Automatic) from the top one. */
        fun above(level: StreamQuality): StreamQuality? = levels.indexOf(level).takeIf { it > 0 }?.let { levels[it - 1] }

        /** How much of the link a stream may take, and how far above its average its busy stretches go (as the server's AwayFromHome). */
        const val HEADROOM = 0.75
        const val PEAK_FACTOR = 1.5

        /** Whether a link of [speed] bits per second carries a stream averaging [bitrate], with the usual headroom. */
        fun fits(bitrate: Long, speed: Long): Boolean = bitrate * PEAK_FACTOR <= speed * HEADROOM
    }
}

/** [this] asking for [quality]: Original as it is, a level by its height and bitrate, Automatic unchanged. */
fun PlaybackRequest.withQuality(quality: StreamQuality): PlaybackRequest = when {
    quality == StreamQuality.Original -> copy(original = true)
    quality.isLevel -> copy(maxHeight = quality.height, maxBitrate = quality.bitrate)
    else -> this
}

/**
 * When a stream on Automatic (not the server's ladder, which moves by itself) steps down a quality or back up
 * (docs/remote-streaming.md). Down: two stalls within a minute, or one over 10 seconds, not counting the first 3 seconds
 * after a start or seek. Up: after 5 minutes without a stall, when the link fits the quality above, at most every 5
 * minutes. The lowered quality lasts for the title; [reset] for the next one.
 */
class QualitySteps(private val now: () -> Long = System::currentTimeMillis) {
    /** The quality stepped down to, or null when not lowered. */
    var lowered: StreamQuality? = null
        private set

    /** Stalls not counted before this (a start or seek a moment ago). */
    private var ignoreUntil = 0L
    private var stallStart: Long? = null
    private val stalls = ArrayDeque<Long>()
    private var lastStall = 0L
    private var lastStep = 0L
    private var playingSince = 0L

    /** Another title: not lowered, nothing counted. */
    fun reset() {
        lowered = null
        stalls.clear()
        stallStart = null
        lastStall = 0L
        lastStep = 0L
        playingSince = 0L
        started()
    }

    /** A start, reopen or seek just now: the next few seconds' waiting is the stream filling, not a stall. */
    fun started() {
        val at = now()
        ignoreUntil = at + IGNORE_AFTER_START_MS
        stallStart = null
        if (playingSince == 0L) playingSince = at
    }

    /** The picture stopped to wait for data. */
    fun stalled() {
        if (stallStart == null) stallStart = now()
    }

    /**
     * Playing again after a stall: whether to step down now (the second within a minute). [playingHeight] is the
     * picture's height, for [StreamQuality.below]; the quality to step to is in [lowered] when this returns true.
     */
    fun resumed(playingHeight: Int?): Boolean {
        val began = stallStart ?: return false
        stallStart = null
        val at = now()
        val counted = maxOf(began, ignoreUntil)
        if (at <= counted) return false
        lastStall = at
        stalls.addLast(at)
        while (stalls.isNotEmpty() && at - stalls.first() > STALL_WINDOW_MS) stalls.removeFirst()
        return if (stalls.size >= STALLS_TO_STEP) stepDown(playingHeight) else false
    }

    /** Still stalled: whether this one has gone on long enough to step down (once over 10 seconds). */
    fun stillStalled(playingHeight: Int?): Boolean {
        val began = stallStart ?: return false
        val at = now()
        if (at - maxOf(began, ignoreUntil) < LONG_STALL_MS) return false
        stallStart = null
        lastStall = at
        return stepDown(playingHeight)
    }

    private fun stepDown(playingHeight: Int?): Boolean {
        val below = StreamQuality.below(minOf(playingHeight ?: Int.MAX_VALUE, lowered?.height ?: Int.MAX_VALUE).takeIf { it != Int.MAX_VALUE })
            ?: return false
        lowered = below
        stalls.clear()
        lastStep = now()
        started()
        return true
    }

    /**
     * Whether a lowered stream can go back up now, given the link's [speed] (bits per second) and the file's own
     * average bitrate ([fileBitrate], for going back to no limit from the top level). The quality stepped to is in
     * [lowered] (null: back to Automatic's own) when this returns true.
     */
    fun stepUp(speed: Long?, fileBitrate: Long?): Boolean {
        val current = lowered ?: return false
        if (speed == null || stallStart != null) return false
        val at = now()
        if (at - maxOf(lastStall, lastStep, playingSince) < SMOOTH_MS) return false
        val above = StreamQuality.above(current)
        val needs = above?.bitrate ?: fileBitrate ?: StreamQuality.levels.first().bitrate!!
        if (!StreamQuality.fits(needs, speed)) return false
        lowered = above
        lastStep = at
        started()
        return true
    }

    companion object {
        const val IGNORE_AFTER_START_MS = 3_000L
        const val STALL_WINDOW_MS = 60_000L
        const val STALLS_TO_STEP = 2
        const val LONG_STALL_MS = 10_000L
        const val SMOOTH_MS = 5 * 60_000L
    }
}
