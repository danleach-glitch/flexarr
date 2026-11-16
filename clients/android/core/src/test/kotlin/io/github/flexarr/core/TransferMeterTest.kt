package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TransferMeterTest {
    @Test
    fun `leaves out the wait for the first byte and pauses`() {
        var clock = 0L
        val samples = mutableListOf<Pair<Long, Long>>()
        val meter = TransferMeter({ clock }) { bytes, millis -> samples += bytes to millis }
        clock = 5_000 // a long wait for the server
        meter.transferred(100_000) // starts the clock only
        repeat(10) { clock += 50; meter.transferred(100_000) }
        clock += 10_000 // paused
        meter.transferred(500_000) // what the connection held: not counted
        repeat(5) { clock += 50; meter.transferred(100_000) }
        meter.ended()
        assertEquals(listOf(1_500_000L to 750L), samples)
    }

    @Test
    fun `a reading every window, and nothing from a burst`() {
        var clock = 0L
        val samples = mutableListOf<Pair<Long, Long>>()
        val meter = TransferMeter({ clock }) { bytes, millis -> samples += bytes to millis }
        meter.transferred(1)
        repeat(30) { clock += 30; meter.transferred(100_000) }
        assertEquals(1, samples.size)
        assertEquals(TransferMeter.WINDOW_BYTES, samples[0].first)
        meter.ended()
        assertEquals(2, samples.size)
        // From memory: a megabyte in a few milliseconds says nothing.
        samples.clear()
        meter.transferred(1)
        repeat(10) { clock += 1; meter.transferred(100_000) }
        meter.ended()
        assertTrue(samples.isEmpty())
    }
}
