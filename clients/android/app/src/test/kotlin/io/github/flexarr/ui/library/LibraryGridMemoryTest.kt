package io.github.flexarr.ui.library

import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.core.Paged
import io.github.flexarr.fixture
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Back from a title, a library's grid has what it had loaded: the same list for the same sort, genre and profile. */
class LibraryGridMemoryTest {
    private val template = fixture<Paged<ItemSummary>>("library-items").items.first()
    private val loaded = (0 until 600).map { template.copy(id = "t$it", title = "Title $it") }

    @Test
    fun `the list loaded so far comes back for the same sort, genre and profile only`() {
        val memory = LibraryGridMemory()
        val key = listOf(LibrarySort.Title, null)
        assertFalse(memory.restore(key, "main"))
        memory.items.addAll(loaded)
        memory.total = 2000
        memory.loadedAt = 1L
        assertTrue(memory.restore(key, "main"))
        assertEquals(600, memory.items.size)
        assertEquals(2000, memory.total)
        // Another sort: its own list, from its first page.
        assertFalse(memory.restore(listOf(LibrarySort.Title, "Drama"), "main"))
        assertTrue(memory.items.isEmpty())
        assertEquals(0, memory.total)
        assertEquals(null, memory.loadedAt)
        memory.items.addAll(loaded)
        // Another profile: never the last one's list.
        assertFalse(memory.restore(listOf(LibrarySort.Title, "Drama"), "child"))
        assertTrue(memory.items.isEmpty())
    }
}
