package io.github.flexarr

import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.i18n.Words
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class RefreshFailureTest {
    private var clock = 1_000L

    @Test
    fun `a server that answered is never said to be out of reach`() {
        assertEquals("Enter the admin PIN.", refreshFailure(FlexarrException.ParentPinNeeded(null)))
        assertEquals("Not allowed", refreshFailure(FlexarrException.Http(403, "Not allowed")))
    }

    @Test
    fun `anything that isn't the server's answer says it didn't answer`() {
        assertEquals(Words.serverDidntAnswer, refreshFailure(IllegalStateException("boom")))
    }

    @Test
    fun `a refresh that ended while nobody was looking is said once, when they're back`() {
        val end = UnseenEnd<String, String>(keptMs = 120_000, now = { clock })
        assertNull(end.take("main"))
        end.ended("main", "It failed")
        assertEquals("It failed", end.take("main"))
        assertNull(end.take("main"))
    }

    @Test
    fun `a refresh someone else asked for isn't said to whoever's here now`() {
        val end = UnseenEnd<String, String>(keptMs = 120_000, now = { clock })
        end.ended("main", "It failed")
        assertNull(end.take("kids"))
        assertNull(end.take("main"))
    }

    @Test
    fun `a result nobody came back to in two minutes is dropped, success or failure`() {
        val end = UnseenEnd<String, String>(keptMs = 120_000, now = { clock })
        end.ended("main", "It failed")
        clock += 120_000
        assertEquals("It failed", end.take("main"))
        end.ended("main", "Guide refreshed")
        clock += 120_001
        assertNull(end.take("main"))
        // Dropped, not just hidden.
        clock = 1_000
        assertNull(end.take("main"))
    }

    @Test
    fun `a result's age can run from when it was last known to be running`() {
        val end = UnseenEnd<String, String>(keptMs = 120_000, now = { clock })
        clock = 500_000
        end.ended("main", "Guide refreshed", at = 100_000)
        assertNull(end.take("main"))
    }
}
