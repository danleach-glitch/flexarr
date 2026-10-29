package io.github.flexarr.ui.library

import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.Paged
import io.github.flexarr.fixture
import java.io.IOException
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** A library's list fetched again after playback or a while away, as the iPhone's `fetchLoaded` does. */
class RefetchTest {
    private val template = fixture<Paged<ItemSummary>>("library-items").items.first()

    private fun titles(vararg ids: String) = ids.map { template.copy(id = it) }

    /** A server listing [library], answering every page asked for (and failing the ones in [failing]). */
    private fun server(library: List<ItemSummary>, failing: Set<Int> = emptySet(), asked: MutableList<Pair<Int, Int>> = mutableListOf()) =
        { skip: Int, take: Int ->
            asked += skip to take
            if (skip in failing) throw IOException("offline")
            Paged(library.size, library.drop(skip).take(take))
        }

    @Test
    fun `titles removed since go and ones added come in`() = runTest {
        val shown = titles("a", "b", "c")
        val fresh = refetch(shown.size, server(titles("a", "new", "c")))!!
        assertEquals(listOf("a", "new", "c"), fresh.replacing(shown).map { it.id })
        assertEquals(3, fresh.total)
        assertTrue(fresh.upToDate)
    }

    @Test
    fun `a long list comes back a page at a time`() = runTest {
        val library = (1..1200).map { template.copy(id = "t$it") }
        val asked = mutableListOf<Pair<Int, Int>>()
        val fresh = refetch(700, server(library, asked = asked))!!
        assertEquals(listOf(0 to 500, 500 to 200), asked)
        // The pages reached as far as was shown; the rest of the list is still to scroll to.
        val shown = library.take(700)
        assertEquals(shown, fresh.replacing(shown))
        assertFalse(fresh.complete)
        assertTrue(fresh.upToDate)
    }

    @Test
    fun `what a failed page would have covered stays as it was`() = runTest {
        val shown = (1..700).map { template.copy(id = "t$it") }
        // t2 has gone, so t501 moves up into the first page.
        val library = shown.filter { it.id != "t2" }
        val fresh = refetch(shown.size, server(library, failing = setOf(500)))!!
        val replaced = fresh.replacing(shown)
        // The first page without t2, then the rest as shown, each title once.
        assertEquals(shown.map { it.id } - "t2", replaced.map { it.id })
        assertFalse(fresh.upToDate)
    }

    @Test
    fun `a title shifted into the next page is listed once`() = runTest {
        val fresh = refetch(4) { skip, _ ->
            // "b" is in both pages: something was added in between.
            if (skip == 0) Paged(4, titles("a", "b")) else Paged(4, titles("b", "c"))
        }!!
        assertEquals(listOf("a", "b", "c"), fresh.items.map { it.id })
    }

    @Test
    fun `nothing back changes nothing`() = runTest {
        assertNull(refetch(3, server(titles("a"), failing = setOf(0))))
    }
}
