package io.github.flexarr.ui.components

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TitleArtTest {
    @Test
    fun `a short title keeps its size`() {
        assertEquals(52, titleArtFontSize("Fargo", 52))
        assertEquals(52, titleArtFontSize("The Grand Budapest", 52))
    }

    @Test
    fun `a longer title steps down, a very long one further`() {
        val medium = titleArtFontSize("The Lord of the Rings", 52)
        val long = titleArtFontSize("The Extraordinarily Long-Titled Adventures of Someone", 52)
        assertTrue(medium < 52)
        assertTrue(long < medium)
        assertEquals(33, long)
    }
}
