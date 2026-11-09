package io.github.flexarr

import io.github.flexarr.core.FlexarrException
import org.junit.Assert.assertEquals
import org.junit.Test

class RefreshFailureTest {
    @Test
    fun `a server that answered is never said to be out of reach`() {
        assertEquals("Enter the admin PIN.", refreshFailure(FlexarrException.ParentPinNeeded(null)))
        assertEquals("Not allowed", refreshFailure(FlexarrException.Http(403, "Not allowed")))
    }

    @Test
    fun `anything that isn't the server's answer says it couldn't be reached`() {
        assertEquals("Couldn’t reach the server.", refreshFailure(IllegalStateException("boom")))
    }
}
