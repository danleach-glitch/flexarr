package io.github.flexarr.core

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException

class FailureKindTest {
    @Test
    fun `a 404 is not found, not a network failure`() {
        val gone = FlexarrException.Http(404, null)
        assertTrue(gone.isNotFound)
        assertFalse(gone.isNetworkFailure)
    }

    @Test
    fun `other server errors are neither`() {
        val broken = FlexarrException.Http(500, null)
        assertFalse(broken.isNotFound)
        assertFalse(broken.isNetworkFailure)
    }

    @Test
    fun `an unreachable server is a network failure`() {
        assertTrue(FlexarrException.Unreachable(IOException("refused")).isNetworkFailure)
        assertTrue(IOException("reset").isNetworkFailure)
        assertFalse(FlexarrException.Unreachable(IOException("refused")).isNotFound)
    }
}
