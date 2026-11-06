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

    @Test fun liveTvKeepsExoPlayersOwnControlUnlessMemoryIsShort() {
        // Nothing to cap: the player is built as it always was.
        assertNull(StartBuffer.cappedDefault(maxHeapBytes = 256 * mb))
        assertNull(StartBuffer.cappedDefault(maxHeapBytes = Long.MAX_VALUE))
        assertNotNull(StartBuffer.cappedDefault(maxHeapBytes = 192 * mb))
        assertNotNull(StartBuffer.cappedDefault(maxHeapBytes = 128 * mb))
    }

    @Test fun theLimitIsTheSmallestHeapThatStillFitsExoPlayersBuffer() {
        // 144 MB and a bit for the buffer, 112 MB for the app: just under that is capped, at it nothing is.
        val fits = 112 * mb + androidx.media3.exoplayer.DefaultLoadControl.DEFAULT_MUXED_BUFFER_SIZE
        assertNull(StartBuffer.targetBufferBytes(fits))
        assertEquals(androidx.media3.exoplayer.DefaultLoadControl.DEFAULT_MUXED_BUFFER_SIZE - 1, StartBuffer.targetBufferBytes(fits - 1))
        // Nonsense from the runtime still gives a buffer that plays.
        assertEquals((32 * mb).toInt(), StartBuffer.targetBufferBytes(0))
        assertEquals((32 * mb).toInt(), StartBuffer.targetBufferBytes(-1))
    }
}
