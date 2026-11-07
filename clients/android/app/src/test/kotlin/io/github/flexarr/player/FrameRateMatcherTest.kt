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
}
