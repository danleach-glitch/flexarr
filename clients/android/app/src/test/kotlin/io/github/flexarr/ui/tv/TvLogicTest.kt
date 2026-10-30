package io.github.flexarr.ui.tv

import io.github.flexarr.isSwitch
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import java.time.Duration
import java.time.Instant

@RunWith(RobolectricTestRunner::class)
class TvLogicTest {
    private val now = Instant.parse("2026-10-30T16:17:00Z")

    @Test
    fun theGuideStartsOnTheHalfHour() {
        assertEquals(Instant.parse("2026-10-30T16:00:00Z"), GuideWindow.halfHourBefore(now))
        assertEquals(Instant.parse("2026-10-30T16:30:00Z"), GuideWindow.halfHourBefore(Instant.parse("2026-10-30T16:30:00Z")))
    }

    @Test
    fun theGuideGoesBackNoMoreThanThreeHours() {
        // A guide from yesterday: three hours ago, on the half hour.
        assertEquals(Instant.parse("2026-10-30T13:00:00Z"), GuideWindow.earliest(now.minus(Duration.ofDays(1)), now))
        // A guide from an hour ago: there.
        assertEquals(Instant.parse("2026-10-30T15:00:00Z"), GuideWindow.earliest(now.minus(Duration.ofHours(1)), now))
    }

    @Test
    fun movingStaysWithinTheGuide() {
        val earliest = Instant.parse("2026-10-30T15:00:00Z")
        val until = Instant.parse("2026-10-30T22:00:00Z")
        val start = Instant.parse("2026-10-30T16:00:00Z")
        assertEquals(Instant.parse("2026-10-30T17:30:00Z"), GuideWindow.moved(start, GuideWindow.STEP, earliest, until))
        // Not before the earliest, nor so late the last three hours run past the end.
        assertEquals(earliest, GuideWindow.moved(start, GuideWindow.STEP.negated(), earliest, until))
        assertEquals(Instant.parse("2026-10-30T19:00:00Z"), GuideWindow.moved(Instant.parse("2026-10-30T18:30:00Z"), GuideWindow.STEP, earliest, until))
        // A guide shorter than the window: it stays at the start.
        assertEquals(earliest, GuideWindow.moved(start, GuideWindow.STEP, earliest, Instant.parse("2026-10-30T16:00:00Z")))
    }

    @Test
    fun moreOfTheGuideIsAskedForNearItsEnd() {
        val until = Instant.parse("2026-10-30T22:00:00Z")
        assertFalse(GuideWindow.needsLater(Instant.parse("2026-10-30T16:00:00Z"), until))
        assertTrue(GuideWindow.needsLater(Instant.parse("2026-10-30T17:30:00Z"), until))
    }

    @Test
    fun aScreenIsOpenedAgainByItsRoute() {
        val args = mapOf("id" to "42", "name" to "Frances McDormand", "sort" to "title", "genre" to "Action/Adventure")
        assertEquals("person/42?name=Frances%20McDormand", fillRoute("person/{id}?name={name}", args::get))
        assertEquals("library/42?sort=title&genre=Action%2FAdventure", fillRoute("library/{id}?sort={sort}&genre={genre}", args::get))
        // No genre: left empty, which the screen reads as none.
        assertEquals("library/42?sort=title&genre=", fillRoute("library/{id}?sort={sort}&genre={genre}") { if (it == "genre") null else args[it] })
    }

    @Test
    fun onlyChoosingSomeoneElseSwitchesProfile() {
        assertFalse(isSwitch(null, "a"))
        assertFalse(isSwitch("a", "a"))
        assertTrue(isSwitch("a", "b"))
    }
}
