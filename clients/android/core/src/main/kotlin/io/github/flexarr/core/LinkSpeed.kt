package io.github.flexarr.core

/**
 * How fast data reaches this device from the server lately, for the start request's `measuredBitrate`
 * (docs/fast-start.md): each big enough transfer (a stream's, as the players' bandwidth meter counts it) is a sample
 * of its bytes over the time they took. The median of the last few, for half an hour.
 */
class LinkSpeed(private val now: () -> Long = System::currentTimeMillis) {
    private class Sample(val bitsPerSecond: Long, val at: Long)

    private val samples = ArrayDeque<Sample>()

    /** Notes [bytes] arriving over [millis]. */
    @Synchronized
    fun sample(bytes: Long, millis: Long) {
        if (bytes < MIN_SAMPLE_BYTES || millis <= 0) return
        val at = now()
        samples.removeAll { at - it.at >= FRESH_MS }
        samples.addLast(Sample(bytes * 8 * 1000 / millis, at))
        while (samples.size > KEPT) samples.removeFirst()
    }

    /** The measured speed in bits per second, or null with nothing recent to go on. */
    @Synchronized
    fun current(): Long? {
        val at = now()
        val fresh = samples.filter { at - it.at < FRESH_MS }.map { it.bitsPerSecond }.sorted()
        return fresh.getOrNull(fresh.size / 2)
    }

    companion object {
        /** Transfers smaller than this are mostly the round trip, not the speed. */
        const val MIN_SAMPLE_BYTES = 200_000L
        const val KEPT = 8
        const val FRESH_MS = 30 * 60_000L
    }
}
