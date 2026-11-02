package io.github.flexarr.player

import androidx.compose.runtime.staticCompositionLocalOf

/**
 * How a sitting in the player ended, so the page Play was pressed on can move on: that page ([from], a token it made),
 * the title it was started with, the one it stopped on, whether that one was watched, and the episode after it.
 */
data class PlaybackEnding(
    val from: String?,
    val startedWith: String,
    val stoppedOn: String,
    val watched: Boolean,
    val next: String?,
) {
    /**
     * Where a page showing [shown] goes now: past an episode watched to its end, or on to the one the player had moved
     * on to and stopped part way through. Null to stay (a show's page, the last episode, stopped part way).
     */
    fun moveOn(shown: String): String? {
        if (shown != startedWith) return null
        return (if (watched) next else stoppedOn)?.takeIf { it != startedWith }
    }

    companion object {
        /** Watched as the server counts it: into the credits or past 92%, whichever comes first. */
        fun isWatched(position: Double, duration: Double, creditsStart: Double?): Boolean =
            position > 0 && position >= minOf(creditsStart ?: Double.POSITIVE_INFINITY, if (duration > 0) duration * 0.92 else Double.POSITIVE_INFINITY)
    }
}

/** The token of the page whose Play buttons are inside, given to the player so that page can move on afterwards. */
val LocalPlayFrom = staticCompositionLocalOf<String?> { null }
