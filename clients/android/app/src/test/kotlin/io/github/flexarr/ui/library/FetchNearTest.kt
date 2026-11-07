package io.github.flexarr.ui.library

import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.Paged
import io.github.flexarr.fixture
import java.io.IOException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** After something is watched, a long list fetches again only what's near the screen and the title just played. */
class FetchNearTest {
    private val template = fixture<Paged<ItemSummary>>("library-items").items.first()
    private val library = (0 until 3000).map { template.copy(id = "t$it", title = "Title $it") }

    private fun server(list: List<ItemSummary>, asked: MutableList<Pair<Int, Int>>, failing: Boolean = false) = { skip: Int, take: Int ->
        asked += skip to take
        if (failing) throw IOException("offline")
        Paged(list.size, list.drop(skip).take(take))
    }

    @Test
    fun `deep in a long list only the titles near the screen come down again`() = runTest {
        val asked = mutableListOf<Pair<Int, Int>>()
        val watched = library.map { if (it.id == "t2010") it.copy(title = "Watched") else it }
        val fresh = fetchNear(library, first = 2000, last = 2023, played = emptyList(), page = server(watched, asked))!!
        assertEquals(listOf(2000 - NEAR_SCREEN to 24 + 2 * NEAR_SCREEN), asked)
        val updated = library.updatedFrom(fresh)
        assertEquals("Watched", updated[2010].title)
        assertEquals(library.map { it.id }, updated.map { it.id })
    }

    @Test
    fun `the title just played is fetched too, when it's further away`() = runTest {
        val asked = mutableListOf<Pair<Int, Int>>()
        val fresh = fetchNear(library, first = 0, last = 23, played = listOf("t1500", "not-listed"), page = server(library, asked))!!
        assertEquals(listOf(0 to 24 + NEAR_SCREEN, 1499 to 3), asked)
        assertTrue("t1500" in fresh)
    }

    @Test
    fun `nothing back changes nothing`() = runTest {
        assertNull(fetchNear(library, 0, 10, emptyList(), server(library, mutableListOf(), failing = true)))
        assertNull(fetchNear(emptyList(), 0, 10, emptyList(), server(library, mutableListOf())))
    }

    @Test
    fun `a short list is fetched within its end`() = runTest {
        val asked = mutableListOf<Pair<Int, Int>>()
        val short = library.take(10)
        fetchNear(short, first = 0, last = 9, played = emptyList(), page = server(short, asked))
        assertEquals(listOf(0 to 10), asked)
    }
}
