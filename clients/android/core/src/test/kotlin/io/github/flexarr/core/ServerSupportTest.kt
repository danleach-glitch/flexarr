package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The same cases as FlexarrKit's ServerSupportTests. */
class ServerSupportTest {
    @Test
    fun olderThanTheMinimumIsTooOld() {
        assertTrue(ServerSupport.isTooOld("0.49.9"))
        assertTrue(ServerSupport.isTooOld("0.23.0"))
        assertTrue(ServerSupport.isTooOld("0.49.2+d4c892c6190c"))
        assertTrue(ServerSupport.isTooOld("0.49"))
    }

    @Test
    fun theMinimumAndNewerAreFine() {
        assertFalse(ServerSupport.isTooOld("0.50.0"))
        assertFalse(ServerSupport.isTooOld("0.50.1"))
        assertFalse(ServerSupport.isTooOld("0.59.0+d4c892c6190c"))
        assertFalse(ServerSupport.isTooOld("1.0.0"))
        assertFalse(ServerSupport.isTooOld("v0.51.0"))
    }

    @Test
    fun devBuildsCountByTheirNumbers() {
        assertFalse(ServerSupport.isTooOld("0.60.0-dev.1a2b3c4"))
        assertFalse(ServerSupport.isTooOld("0.50.0-dev.1a2b3c4+abc"))
        assertTrue(ServerSupport.isTooOld("0.49.0-dev.1a2b3c4"))
    }

    @Test
    fun versionsThatDontReadAreNeverTooOld() {
        assertFalse(ServerSupport.isTooOld(null))
        assertFalse(ServerSupport.isTooOld(""))
        assertFalse(ServerSupport.isTooOld("dev"))
        assertFalse(ServerSupport.isTooOld("0"))
        assertFalse(ServerSupport.isTooOld("0.x.1"))
        assertFalse(ServerSupport.isTooOld("0..1"))
        assertFalse(ServerSupport.isTooOld("0.1.2.3"))
    }

    @Test
    fun theMinimumReads() {
        assertEquals(listOf(0, 50, 0), ServerSupport.numbers(ServerSupport.MINIMUM_VERSION))
    }
}
