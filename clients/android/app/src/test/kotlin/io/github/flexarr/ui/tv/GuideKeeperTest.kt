package io.github.flexarr.ui.tv

import io.github.flexarr.core.LiveGuide
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant

/** The TV's kept guide: given again while it's recent, and not held once it's no use. */
@OptIn(ExperimentalCoroutinesApi::class)
class GuideKeeperTest {
    private val guide = LiveGuide(Instant.parse("2026-11-06T16:00:00Z"), Instant.parse("2026-11-06T22:00:00Z"), emptyList())
    private var fetches = 0

    private suspend fun GuideKeeper.ask(hours: Int = 6, sign: String = "http://server p", fresh: Boolean = false) =
        guide(sign, hours, fresh) {
            fetches++
            guide
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
}
