package io.github.flexarr.ui.tv

import io.github.flexarr.core.GuideChannel
import io.github.flexarr.core.GuideFetch
import io.github.flexarr.core.GuideFilter
import io.github.flexarr.core.LiveGuide
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

/** The TV's kept guide: given again while it's recent, and not held once it's no use. */
@OptIn(ExperimentalCoroutinesApi::class)
class GuideKeeperTest {
    private val guide = LiveGuide(Instant.parse("2026-11-06T16:00:00Z"), Instant.parse("2026-11-06T22:00:00Z"), emptyList())
    private var fetches = 0
    private val tags = mutableListOf<String?>()

    private suspend fun GuideKeeper.ask(
        hours: Int = 6,
        sign: String = "http://server p",
        fresh: Boolean = false,
        filter: GuideFilter = GuideFilter.Everything,
        covers: List<String> = emptyList(),
        have: KeptGuide? = null,
        answer: (String?) -> GuideFetch = { GuideFetch(guide, "\"g$fetches\"") },
    ) = guide(sign, "$sign $hours ${filter.query}", filter, hours, fresh, covers, have) { etag ->
        fetches++
        tags += etag
        answer(etag)
    }

    private fun TestScope.wait(minutes: Long) {
        advanceTimeBy(minutes * 60_000)
        runCurrent()
    }

    @Test
    fun `a guide just fetched is given again without fetching`() = runTest {
        val keeper = GuideKeeper(backgroundScope)

        keeper.ask()
        keeper.ask()
        keeper.ask(hours = 3)

        assertEquals(1, fetches)
        assertTrue(keeper.holds)
    }

    @Test
    fun `more hours, another profile or a change fetches`() = runTest {
        val keeper = GuideKeeper(backgroundScope)

        keeper.ask()
        keeper.ask(hours = 12)
        keeper.ask(hours = 12, sign = "http://server q")
        keeper.ask(hours = 12, sign = "http://server q", fresh = true)

        assertEquals(4, fetches)
    }

    @Test
    fun `a guide too old to be given again is let go`() = runTest {
        val keeper = GuideKeeper(backgroundScope)
        keeper.ask()

        wait(minutes = 3)
        assertTrue(keeper.holds)
        wait(minutes = 1)

        assertFalse(keeper.holds)
        keeper.ask()
        assertEquals(2, fetches)
    }

    @Test
    fun `a newer guide is kept its own four minutes`() = runTest {
        val keeper = GuideKeeper(backgroundScope)
        keeper.ask()
        wait(minutes = 3)
        keeper.ask(fresh = true)

        // The first one's time is up, but it's the second that's kept.
        wait(minutes = 3)
        assertTrue(keeper.holds)
        wait(minutes = 1)

        assertFalse(keeper.holds)
    }

    @Test
    fun `asked for memory back, the guide goes at once and the next is fetched`() = runTest {
        val keeper = GuideKeeper(backgroundScope)
        keeper.ask()

        keeper.clear()

        assertFalse(keeper.holds)
        keeper.ask()
        assertEquals(2, fetches)
        // And clearing with nothing kept is fine.
        keeper.clear()
        keeper.clear()
        assertFalse(keeper.holds)
    }

    private fun channels(vararg ids: String) = LiveGuide(guide.from, guide.until, ids.map { GuideChannel(it, 1.0, it, programmes = emptyList()) })
    private val favourites = GuideFilter(mapOf("favourites" to "true"))

    @Test
    fun `a kept guide of every channel, or with every channel shown, does for another category`() = runTest {
        val keeper = GuideKeeper(backgroundScope)
        keeper.ask(answer = { GuideFetch(channels("a", "b", "c"), null) })

        // Every channel: any narrower question is in it.
        keeper.ask(filter = favourites, covers = listOf("a"))
        assertEquals(1, fetches)

        // A narrower one: given again for channels it has, fetched for one it hasn't.
        keeper.clear()
        keeper.ask(filter = GuideFilter(mapOf("channels" to "a,b")), answer = { GuideFetch(channels("a", "b"), null) })
        keeper.ask(filter = GuideFilter(mapOf("channels" to "b")), covers = listOf("b"))
        assertEquals(2, fetches)
        keeper.ask(filter = GuideFilter(mapOf("channels" to "c")), covers = listOf("c"))
        assertEquals(3, fetches)
    }

    @Test
    fun `asked again with the guide on screen's ETag, an unchanged guide is kept`() = runTest {
        val keeper = GuideKeeper(backgroundScope)
        val first = keeper.ask(filter = favourites)
        assertNull(tags.single())

        // Too old to give again: asked with its ETag, and a 304 (no guide back) gives the one on screen.
        wait(minutes = 5)
        val again = keeper.ask(filter = favourites, have = first, answer = { GuideFetch(null, it) })
        assertEquals("\"g1\"", tags.last())
        assertSame(first, again)
        assertTrue(keeper.holds)

        // A different question, or something changed (a recording set): no ETag.
        wait(minutes = 5)
        keeper.ask(filter = GuideFilter.Everything, have = first)
        assertNull(tags.last())
        keeper.ask(filter = favourites, have = first, fresh = true)
        assertNull(tags.last())
        assertEquals(4, fetches)
    }

    @Test
    fun `a guide covers a question it was asked, every channel's, or one with every channel shown`() {
        val some = KeptGuide(channels("a", "b"), GuideFilter(mapOf("channels" to "a,b")), "ask", null)
        assertTrue(some.covers(GuideFilter(mapOf("channels" to "a,b")), listOf("a", "b")))
        assertTrue(some.covers(favourites, listOf("b")))
        assertFalse(some.covers(favourites, listOf("b", "c")))
        // Nothing shown is no proof it has the channels of another question.
        assertFalse(some.covers(GuideFilter.Everything, emptyList()))
        assertTrue(KeptGuide(channels(), GuideFilter.Everything, "ask", null).covers(favourites, listOf("z")))
    }
}
