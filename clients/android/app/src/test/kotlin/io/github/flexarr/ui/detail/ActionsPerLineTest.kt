package io.github.flexarr.ui.detail

import org.junit.Assert.assertEquals
import org.junit.Test

class ActionsPerLineTest {
    @Test
    fun `at the usual text sizes every action shares one line`() {
        assertEquals(4, actionsPerLine(1.0f, 4))
        assertEquals(5, actionsPerLine(1.3f, 5))
    }

    @Test
    fun `large text puts fewer to a line`() {
        assertEquals(3, actionsPerLine(1.5f, 4))
        assertEquals(2, actionsPerLine(2.0f, 4))
    }

    @Test
    fun `a page with fewer actions than a line holds keeps them all`() {
        assertEquals(1, actionsPerLine(2.0f, 1))
        assertEquals(1, actionsPerLine(1.0f, 0))
    }
}
