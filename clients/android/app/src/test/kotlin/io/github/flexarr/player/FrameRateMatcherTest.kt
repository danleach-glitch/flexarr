package io.github.flexarr.player

import androidx.media3.common.Format
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** Which frame rates a TV is asked to switch to. */
class FrameRateMatcherTest {
    private fun format(rate: Float) = Format.Builder().setFrameRate(rate).build()

    @Test fun filmRates_areAskedFor() {
        assertEquals(23.976f, FrameRateMatcher.usable(format(23.976f)))
        assertEquals(25f, FrameRateMatcher.usable(format(25f)))
        assertEquals(59.94f, FrameRateMatcher.usable(format(59.94f)))
    }

    @Test fun unknownOrOddRates_leaveTheScreenAlone() {
        assertNull(FrameRateMatcher.usable(format(Format.NO_VALUE.toFloat())))
        assertNull(FrameRateMatcher.usable(format(1f)))
        assertNull(FrameRateMatcher.usable(format(1000f)))
    }

    private fun frames(fps: Double, count: Int = FrameRateMatcher.FRAMES, start: Long = 0) =
        (0 until count).map { start + Math.round(it * 1_000_000.0 / fps) }

    @Test fun measuredRates_snapToTheRatesFilmsAreMadeAt() {
        assertEquals(23.976f, FrameRateMatcher.measured(frames(24000.0 / 1001)))
        assertEquals(24f, FrameRateMatcher.measured(frames(24.0)))
        assertEquals(25f, FrameRateMatcher.measured(frames(25.0, start = 3_600_000_000)))
        assertEquals(59.94f, FrameRateMatcher.measured(frames(60000.0 / 1001)))
    }

    @Test fun mkvMillisecondTimes_stillTell23976From24() {
        fun ms(fps: Double) = frames(fps).map { Math.round(it / 1000.0) * 1000 }
        assertEquals(23.976f, FrameRateMatcher.measured(ms(24000.0 / 1001)))
        assertEquals(24f, FrameRateMatcher.measured(ms(24.0)))
        assertEquals(29.97f, FrameRateMatcher.measured(ms(30000.0 / 1001)))
        assertEquals(30f, FrameRateMatcher.measured(ms(30.0)))
    }

    @Test fun droppedFrames_stillCount() {
        val dropped = frames(24000.0 / 1001, count = FrameRateMatcher.FRAMES + 3).filterIndexed { i, _ -> i != 10 && i != 30 && i != 31 }
        assertEquals(23.976f, FrameRateMatcher.measured(dropped.map { Math.round(it / 1000.0) * 1000 }))
    }

    @Test fun tooFewOrUnevenFrames_leaveTheScreenAlone() {
        assertNull(FrameRateMatcher.measured(frames(24.0, count = FrameRateMatcher.FRAMES - 1)))
        // A variable frame rate: gaps that aren't whole frames.
        assertNull(FrameRateMatcher.measured(frames(24.0).mapIndexed { i, t -> if (i % 3 == 0) t + 15_000 else t }))
        // Nothing near a standard rate.
        assertNull(FrameRateMatcher.measured(frames(17.0)))
    }
}
