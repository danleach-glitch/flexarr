package io.github.flexarr.core

/**
 * Measures how fast one download's bytes arrive while they flow (docs/remote-streaming.md), for [LinkSpeed]: the wait
 * for the first byte isn't counted (the first chunk only starts the clock), nor is a pause (a gap over [GAP_MS], as when
 * a file played as it is waits for the player's buffer to empty), nor the chunk after one, which may only be what the
 * connection held meanwhile. Every [WINDOW_BYTES] of flowing data, and at the end, is one reading if it's big enough
 * and took long enough to be the link rather than a burst from memory.
 */
class TransferMeter(private val now: () -> Long, private val onSample: (bytes: Long, millis: Long) -> Unit) {
    private var lastAt: Long? = null
    private var bytes = 0L
    private var millis = 0L

    @Synchronized
    fun transferred(count: Int) {
        val at = now()
        val last = lastAt
        lastAt = at
        if (last == null) return
        val gap = at - last
        if (gap > GAP_MS) return
        bytes += count
        millis += gap
        if (bytes >= WINDOW_BYTES) flush()
    }

    @Synchronized
    fun ended() {
        flush()
        lastAt = null
    }

    private fun flush() {
        if (bytes >= LinkSpeed.MIN_SAMPLE_BYTES && millis >= MIN_FLOWING_MS) onSample(bytes, millis)
        bytes = 0
        millis = 0
    }

    companion object {
        const val GAP_MS = 200L
        const val WINDOW_BYTES = 2_000_000L
        const val MIN_FLOWING_MS = 250L
    }
}
