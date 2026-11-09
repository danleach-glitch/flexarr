package io.github.flexarr

import io.github.flexarr.core.FlexarrException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
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

    @Test
    fun `a refresh that ended while nobody was looking is said once, when they're back`() {
        val end = UnseenEnd<String, String>()
        assertNull(end.take("main"))
        end.ended("main", "It failed")
        assertEquals("It failed", end.take("main"))
        assertNull(end.take("main"))
    }

    @Test
    fun `a refresh someone else asked for isn't said to whoever's here now`() {
        val end = UnseenEnd<String, String>()
        end.ended("main", "It failed")
        assertNull(end.take("kids"))
        assertNull(end.take("main"))
    }
}
