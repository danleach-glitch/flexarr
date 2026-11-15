package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

class ByteSizeTest {
    @Test
    fun `a size keeps its number and unit together`() {
        assertEquals("276 MB", Format.bytes(276_000_000))
        assertEquals("1.2 GB", Format.bytes(1_200_000_000))
        assertEquals("12 KB", Format.bytes(12_000))
        assertFalse(Format.bytes(276_000_000).contains(' '))
    }
}
