package io.github.flexarr.ui.search

import org.junit.Assert.assertEquals
import org.junit.Test

class SearchShownTest {
    @Test
    fun nothingTypedIsIdle() {
        assertEquals(SearchShown.Idle, searchShown("", "far" to 1, null))
    }

    @Test
    fun aFirstSearchInFlightSaysSearching() {
        assertEquals(SearchShown.Searching, searchShown<Int>("fargo", null, null))
    }

    @Test
    fun aNewerFailureBeatsOlderResults() {
        assertEquals(SearchShown.Failed("Offline"), searchShown("fargo", "far" to 1, "fargo" to "Offline"))
    }

    @Test
    fun anOlderFailureDoesNotHideResults() {
        assertEquals(SearchShown.Found("fargo", 2), searchShown("fargo", "fargo" to 2, "far" to "Offline"))
    }

    @Test
    fun olderResultsStandWhileTheNewestAreOnTheirWay() {
        assertEquals(SearchShown.Found("far", 1), searchShown("fargo", "far" to 1, null))
    }
}
