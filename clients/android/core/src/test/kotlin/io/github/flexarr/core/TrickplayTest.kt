package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TrickplayTest {
    private val manifest = TrickplayManifest(interval = 10, width = 320, height = 180, columns = 10, rows = 10, count = 250, sheets = 3)

    @Test
    fun `finds the sheet and the corner of a thumbnail`() {
        assertEquals(TrickplayManifest.Place(0, 0, 0), manifest.thumbnail(0.0))
        assertEquals(TrickplayManifest.Place(0, 320 * 3, 180 * 1), manifest.thumbnail(135.0))
        assertEquals(TrickplayManifest.Place(1, 0, 0), manifest.thumbnail(1000.0))
    }

    @Test
    fun `past the end is the last thumbnail, and nonsense is none`() {
        assertEquals(TrickplayManifest.Place(2, 320 * 9, 180 * 4), manifest.thumbnail(99_999.0))
        assertNull(manifest.thumbnail(Double.NaN))
        assertNull(manifest.copy(interval = 0).thumbnail(5.0))
    }
}
