package io.github.flexarr.ui.settings

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LabelAndValueTest {
    @Test
    fun `a name and value that fit sit side by side`() {
        assertTrue(sideBySide(labelWidth = 200, valueWidth = 80, gap = 12, width = 292))
    }

    @Test
    fun `a name and value too wide together stack`() {
        assertFalse(sideBySide(labelWidth = 200, valueWidth = 81, gap = 12, width = 292))
    }
}
