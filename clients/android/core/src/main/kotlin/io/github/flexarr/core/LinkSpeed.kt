package io.github.flexarr.core

/**
 * How fast data reaches this device from the server lately, for the start request's `measuredBitrate`
 * (docs/fast-start.md): each big enough segment of a video stream is a sample of its bytes over the time they took.
 * Not audio, and not a file played as it is (one long download paced by the player, which measures what was played,
 * not the link). With a few samples, the upper quartile of the last several, for half an hour; with fewer, nothing.
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
        return if (fresh.size >= MIN_SAMPLES) fresh[(fresh.size * 3) / 4] else null
    }

    companion object {
        /** Transfers smaller than this are mostly the round trip, not the speed. */
        const val MIN_SAMPLE_BYTES = 200_000L
        const val KEPT = 12

        /** Fewer samples than this say nothing. */
        const val MIN_SAMPLES = 3
        const val FRESH_MS = 30 * 60_000L
    }
}
