package io.github.flexarr

import org.junit.Assert.assertEquals
import org.junit.Test

/** Music buttons flip from the press, and go back if it didn't work (as Apple's MusicRequesting). */
class MusicMarksTest {
    private val none = MusicMarks(emptySet(), emptySet())

    @Test
    fun `asking shows as asked from the press, even after an earlier unrequest`() {
        assertEquals(MusicMarks(setOf("a"), emptySet()), MusicMarks(emptySet(), setOf("a")).marked(listOf("a"), asAsked = true))
    }

    @Test
    fun `taking back shows as taken back from the press`() {
        assertEquals(MusicMarks(emptySet(), setOf("a")), MusicMarks(setOf("a"), emptySet()).marked(listOf("a"), asAsked = false))
    }

    @Test
    fun `a request that fails goes back to how it was`() {
        val before = none
        assertEquals(before, before.marked(listOf("a", "b"), asAsked = true).restored(listOf("a", "b"), before))
    }

    @Test
    fun `an undo that fails goes back to asked, or to what the server said`() {
        val asked = MusicMarks(setOf("a"), emptySet())
        assertEquals(asked, asked.marked(listOf("a"), asAsked = false).restored(listOf("a"), asked))
        // Requested by the server's word, not here: nothing to remember but that it wasn't undone.
        assertEquals(none, none.marked(listOf("a"), asAsked = false).restored(listOf("a"), none))
    }

    @Test
    fun `a failure leaves others marked meanwhile alone`() {
        val before = none
        val during = before.marked(listOf("a"), asAsked = true).marked(listOf("b"), asAsked = true)
        assertEquals(MusicMarks(setOf("b"), emptySet()), during.restored(listOf("a"), before))
    }
}
