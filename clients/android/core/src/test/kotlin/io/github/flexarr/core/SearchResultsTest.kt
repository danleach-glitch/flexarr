package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SearchResultsTest {
    @Test
    fun `tracks alone are something found`() {
        val results = FlexarrClient.json.decodeFromString(
            SearchResults.serializer(),
            """{"titles":[],"episodes":[],"people":[],"albums":[],"tracks":[{"item":{"id":"t1","title":"Track Number One","kind":"Track","libraryId":"m","childCount":0,"artwork":{},"userData":{"positionSeconds":0,"watched":false,"playCount":0}},"album":"Night Drive"}]}""",
        )
        assertFalse(results.isEmpty)
        assertEquals("Night Drive", results.tracks.single().album)
    }

    @Test
    fun `nothing at all is empty`() {
        assertTrue(SearchResults().isEmpty)
    }
}
