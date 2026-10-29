package io.github.flexarr.downloads

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** A 416 when carrying on a download: finished, or start again. */
class DownloadWorkerTest {
    private fun complete(partBytes: Long, contentRange: String?, expectedBytes: Long? = null) =
        DownloadWorker.isComplete(partBytes, contentRange, expectedBytes)

    @Test
    fun `the whole file is there when the server's total is what's been saved`() {
        assertTrue(complete(1_000, "bytes */1000"))
        assertTrue(complete(1_000, "bytes */1000", expectedBytes = 5))
    }

    @Test
    fun `a part shorter or longer than the server's total starts again`() {
        assertFalse(complete(999, "bytes */1000"))
        assertFalse(complete(1_001, "bytes */1000", expectedBytes = 1_001))
    }

    @Test
    fun `without a total from the server, the size it gave earlier decides`() {
        assertTrue(complete(1_000, null, expectedBytes = 1_000))
        assertTrue(complete(1_000, "bytes */*", expectedBytes = 1_000))
        assertTrue(complete(1_000, "nonsense", expectedBytes = 1_000))
        assertFalse(complete(999, null, expectedBytes = 1_000))
        assertFalse(complete(1_000, null, expectedBytes = null))
    }

    @Test
    fun `nothing saved is never complete`() {
        assertFalse(complete(0, "bytes */0", expectedBytes = 0))
        assertFalse(complete(-1, "bytes */-1"))
    }
}
