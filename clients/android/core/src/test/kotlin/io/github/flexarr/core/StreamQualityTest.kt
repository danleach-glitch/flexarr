package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class StreamQualityTest {
    private val profile = DeviceProfile("android", emptyList(), emptyList(), emptyList(), false, 2160, 6, 0)
    private val request = PlaybackRequest(itemId = "i", profile = profile)

    @Test
    fun `the menu and its requests`() {
        assertEquals(listOf("automatic", "original", "1080p", "720p", "480p"), StreamQuality.menu.map { it.raw })
        assertEquals(request, request.withQuality(StreamQuality.Automatic))
        assertEquals(true, request.withQuality(StreamQuality.Original).original)
        val p720 = request.withQuality(StreamQuality.P720)
        assertEquals(720, p720.maxHeight)
        assertEquals(4_000_000L, p720.maxBitrate)
        assertNull(p720.original)
        assertEquals(StreamQuality.P480, StreamQuality.of("480p"))
        assertEquals(StreamQuality.Automatic, StreamQuality.of("8k"))
    }

    @Test
    fun `the remembered pick is for away from home`() {
        assertEquals(StreamQuality.P720, StreamQuality.forStart(StreamQuality.P720, away = true))
        assertEquals(StreamQuality.Automatic, StreamQuality.forStart(StreamQuality.P720, away = false))
    }

    @Test
    fun `steps below what plays`() {
        assertEquals(StreamQuality.P1080, StreamQuality.below(2160))
        assertEquals(StreamQuality.P720, StreamQuality.below(1080))
        assertEquals(StreamQuality.P480, StreamQuality.below(720))
        assertNull(StreamQuality.below(480))
        assertEquals(StreamQuality.P720, StreamQuality.above(StreamQuality.P480))
        assertNull(StreamQuality.above(StreamQuality.P1080))
    }

    @Test
    fun `fits with headroom and busy stretches`() {
        // 4 Mbps × 1.5 = 6 Mbps, within three quarters of 8 Mbps.
        assertTrue(StreamQuality.fits(4_000_000, 8_000_000))
        assertFalse(StreamQuality.fits(4_000_000, 7_900_000))
    }

    @Test
    fun `two stalls within a minute step down`() {
        var clock = 0L
        val steps = QualitySteps { clock }
        steps.reset()
        clock = 10_000
        steps.stalled(); clock += 1000
        assertFalse(steps.resumed(1080))
        clock += 30_000
        steps.stalled(); clock += 1000
        assertTrue(steps.resumed(1080))
        assertEquals(StreamQuality.P720, steps.lowered)
    }

    @Test
    fun `stalls a minute apart don't`() {
        var clock = 0L
        val steps = QualitySteps { clock }
        steps.reset()
        clock = 10_000
        steps.stalled(); clock += 1000
        assertFalse(steps.resumed(1080))
        clock += 61_000
        steps.stalled(); clock += 1000
        assertFalse(steps.resumed(1080))
        assertNull(steps.lowered)
    }

    @Test
    fun `one long stall steps down`() {
        var clock = 0L
        val steps = QualitySteps { clock }
        steps.reset()
        clock = 10_000
        steps.stalled()
        clock += 9_000
        assertFalse(steps.stillStalled(2160))
        clock += 2_000
        assertTrue(steps.stillStalled(2160))
        assertEquals(StreamQuality.P1080, steps.lowered)
    }

    @Test
    fun `waiting just after a start or seek isn't counted`() {
        var clock = 0L
        val steps = QualitySteps { clock }
        steps.reset()
        steps.stalled(); clock += 2_500
        assertFalse(steps.resumed(1080))
        clock += 1000
        steps.started()
        steps.stalled(); clock += 2_000
        assertFalse(steps.resumed(1080))
        // Only what's past the first 3 seconds counts towards a long stall.
        steps.started()
        steps.stalled()
        clock += 12_000
        assertFalse(steps.stillStalled(1080))
        clock += 1_500
        assertTrue(steps.stillStalled(1080))
    }

    @Test
    fun `nothing below 480p`() {
        var clock = 0L
        val steps = QualitySteps { clock }
        steps.reset()
        clock = 10_000
        steps.stalled(); clock += 11_000
        assertFalse(steps.stillStalled(480))
        assertNull(steps.lowered)
    }

    @Test
    fun `steps back up after five smooth minutes when the link fits`() {
        var clock = 0L
        val steps = QualitySteps { clock }
        steps.reset()
        clock = 10_000
        steps.stalled(); clock += 11_000
        assertTrue(steps.stillStalled(1080))
        assertEquals(StreamQuality.P720, steps.lowered)
        clock += 60_000
        assertFalse(steps.stepUp(50_000_000, null)) // too soon
        clock += 5 * 60_000
        assertFalse(steps.stepUp(10_000_000, null)) // 8 Mbps × 1.5 needs 16 Mbps
        assertFalse(steps.stepUp(null, null))
        assertTrue(steps.stepUp(16_000_000, null))
        assertEquals(StreamQuality.P1080, steps.lowered)
        clock += 60_000
        assertFalse(steps.stepUp(100_000_000, 20_000_000)) // at most every 5 minutes
        clock += 5 * 60_000
        assertFalse(steps.stepUp(30_000_000, 20_000_000)) // the file needs 40 Mbps
        assertTrue(steps.stepUp(40_000_000, 20_000_000))
        assertNull(steps.lowered)
        assertFalse(steps.stepUp(100_000_000, null)) // not lowered
    }

    @Test
    fun `a new title isn't lowered`() {
        var clock = 0L
        val steps = QualitySteps { clock }
        steps.reset()
        clock = 10_000
        steps.stalled(); clock += 11_000
        assertTrue(steps.stillStalled(1080))
        steps.reset()
        assertNull(steps.lowered)
    }
}
