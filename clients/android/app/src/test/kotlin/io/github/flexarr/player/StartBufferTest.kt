package io.github.flexarr.player

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

/** How much the players may buffer, by how much memory the device gives the app. */
class StartBufferTest {
    private val mb = 1024L * 1024

    @Test fun leavesExoPlayersOwnLimitWhereItFits() {
        assertNull(StartBuffer.targetBufferBytes(256 * mb))
        assertNull(StartBuffer.targetBufferBytes(512 * mb))
        // A JVM with no limit says so with the largest number there is.
        assertNull(StartBuffer.targetBufferBytes(Long.MAX_VALUE))
    }

    @Test fun buffersWhatIsLeftOnceTheAppHasItsShare() {
        assertEquals((80 * mb).toInt(), StartBuffer.targetBufferBytes(192 * mb))
        assertEquals((48 * mb).toInt(), StartBuffer.targetBufferBytes(160 * mb))
    }

    @Test fun neverBuffersLessThan32Megabytes() {
        assertEquals((32 * mb).toInt(), StartBuffer.targetBufferBytes(128 * mb))
        assertEquals((32 * mb).toInt(), StartBuffer.targetBufferBytes(64 * mb))
    }

    @Test fun buildsWithAndWithoutALimit() {
        assertNotNull(StartBuffer.loadControl(maxHeapBytes = 128 * mb))
        assertNotNull(StartBuffer.loadControl(maxHeapBytes = 512 * mb))
        assertNotNull(StartBuffer.loadControl())
    }
}
