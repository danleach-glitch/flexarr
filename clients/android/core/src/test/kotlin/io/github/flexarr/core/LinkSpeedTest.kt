package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class LinkSpeedTest {
    @Test
    fun `is the median of recent big transfers`() {
        val speed = LinkSpeed { 0L }
        assertNull(speed.current())
        speed.sample(1_000_000, 1000) // 8 Mbps
        speed.sample(1_000_000, 500) // 16 Mbps
        speed.sample(1_000_000, 2000) // 4 Mbps
        assertEquals(8_000_000L, speed.current())
    }

    @Test
    fun `ignores small transfers and forgets old ones`() {
        var clock = 0L
        val speed = LinkSpeed { clock }
        speed.sample(LinkSpeed.MIN_SAMPLE_BYTES - 1, 10)
        assertNull(speed.current())
        speed.sample(1_000_000, 1000)
        clock = LinkSpeed.FRESH_MS + 1
        assertNull(speed.current())
    }
}
