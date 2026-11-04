package io.github.flexarr.audio

/**
 * When [MusicPlayer] gets the next track ready behind the one playing, as plain rules so they can be tested without a
 * player. The next track's session starts [LEAD_SECONDS] before the current one ends and its stream is queued in the
 * player behind it, so the change of track needs no round trip to the server and an album plays without a gap.
 */
internal object Gapless {
    /** How long before the current track ends the next one is asked for. */
    const val LEAD_SECONDS = 20.0

    /**
     * The queue position that plays when the current track ends on its own, or null when nothing should be got ready:
     * the end of the queue, or repeat one (which starts the same track over the usual way).
     */
    fun upcoming(index: Int, size: Int, repeat: MusicPlayer.RepeatMode): Int? = when {
        size == 0 || index !in 0 until size -> null
        repeat == MusicPlayer.RepeatMode.One -> null
        index + 1 < size -> index + 1
        repeat == MusicPlayer.RepeatMode.All -> 0
        else -> null
    }

    /** Close enough to the end of a track of known length to ask for the next one. */
    fun isTimeToPrepare(position: Double, duration: Double): Boolean =
        duration > 0 && position >= 0 && duration - position <= LEAD_SECONDS
}
