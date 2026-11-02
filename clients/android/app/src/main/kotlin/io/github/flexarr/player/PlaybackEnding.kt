package io.github.flexarr.player

/**
 * How a video ended: the title it was started with (what Play was pressed on), the one it stopped on, whether that
 * one was watched into its credits, and the episode after it.
 */
data class PlaybackEnding(
    val startedWith: String,
    val stoppedOn: String,
    val watched: Boolean,
    val next: String?,
    val at: Long = System.currentTimeMillis(),
) {
    /**
     * Where a page showing the episode it was started with goes now: on past what was finished, or to the episode it
     * stopped part way through. Null to stay.
     */
    val moveOn: String?
        get() = (if (watched) next else stoppedOn)?.takeIf { it != startedWith }
}
