package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset

/** The Coming soon page's days and entries, as FlexarrKit's UpcomingDay. */
class UpcomingDaysTest {
    private val today = LocalDate.of(2026, 11, 2)

    private fun item(id: String, day: Int, hour: Int = 20, kind: MediaKind = MediaKind.Episode, show: String? = "Show", season: Int = 1, episode: Int = 1, available: Boolean? = null, overview: String? = null, library: String? = null) =
        UpcomingItem(id, kind, "Title $id", show, season, episode, Instant.parse("2026-11-%02dT%02d:00:00Z".format(day, hour)), "Airs", libraryItemId = library, overview = overview, available = available)

    @Test
    fun `groups by day from yesterday on, a show's episodes on one day as one entry`() {
        val days = UpcomingDays.days(
            listOf(item("old", 1).copy(date = Instant.parse("2026-10-30T10:00:00Z")), item("a", 2, episode = 1), item("film", 2, hour = 10, kind = MediaKind.Movie, show = null), item("b", 2, episode = 2), item("y", 1)),
            ZoneOffset.UTC,
            today,
        )
        assertEquals(listOf(LocalDate.of(2026, 11, 1), today), days.map { it.date })
        val entries = days[1].entries
        assertEquals(listOf("film", "a"), entries.map { it.first.id })
        assertEquals("S1E1–E2, 2 episodes", entries[1].detail)
        assertEquals("S1E1 Title y", days[0].entries.single().detail)
    }

    @Test
    fun `headings say the day in a word`() {
        assertEquals("Today", UpcomingDays.heading(today, today))
        assertEquals("Tomorrow", UpcomingDays.heading(today.plusDays(1), today))
        assertEquals("Yesterday", UpcomingDays.heading(today.minusDays(1), today))
        assertEquals("Fri 6 Nov", UpcomingDays.heading(today.plusDays(4), today))
    }

    @Test
    fun `status and what opening does`() {
        assertEquals("Ready to watch", UpcomingDays.Entry(listOf(item("a", 2, available = true))).status)
        assertNull(UpcomingDays.Entry(listOf(item("a", 2))).status)
        assertTrue(UpcomingDays.Entry(listOf(item("a", 2, library = "show1"))).hasAction)
        assertTrue(UpcomingDays.Entry(listOf(item("a", 2, overview = "Things happen."))).hasAction)
        assertFalse(UpcomingDays.Entry(listOf(item("a", 2))).hasAction)
    }

    @Test
    fun `older servers' items decode without overview or availability`() {
        val json = """[{"id":"u1","kind":"Episode","title":"Pilot","seriesTitle":"Show","seasonNumber":1,"episodeNumber":1,"date":"2026-11-02T20:00:00Z","when":"Airs"}]"""
        val item = FlexarrClient.json.decodeFromString(kotlinx.serialization.builtins.ListSerializer(UpcomingItem.serializer()), json).single()
        assertNull(item.overview)
        assertNull(item.available)
    }
}
