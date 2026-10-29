package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class SpokenTest {
    @Test
    fun `tells the kinds of request apart`() {
        assertEquals(SpokenRequest.Anything, SpokenRequest.parse(null))
        assertEquals(SpokenRequest.Anything, SpokenRequest.parse(""))
        assertEquals(SpokenRequest.Anything, SpokenRequest.parse("something on Flexarr"))
        assertEquals(SpokenRequest.UpNext, SpokenRequest.parse("my Up Next"))
        assertEquals(SpokenRequest.Continue, SpokenRequest.parse("Continue watching in Flexarr"))
        assertEquals(SpokenRequest.Latest("The Rest Is History"), SpokenRequest.parse("the latest episode of The Rest Is History"))
        assertEquals(SpokenRequest.Latest("Science Weekly"), SpokenRequest.parse("play the newest Science Weekly on Flexarr."))
        assertEquals(SpokenRequest.Title("Curb Your Enthusiasm"), SpokenRequest.parse("the next episode of Curb Your Enthusiasm"))
        assertEquals(SpokenRequest.Title("Heat"), SpokenRequest.parse("Heat"))
    }

    @Test
    fun `matches names closest first`() {
        assertEquals(0, spokenMatch("rest is history", "The Rest Is History"))
        assertEquals(1, spokenMatch("the traitors", "Traitors: Uncloaked"))
        assertEquals(2, spokenMatch("history", "The Rest Is History"))
        assertNull(spokenMatch("history hour", "The Rest Is History"))
        assertNull(spokenMatch("", "Anything"))

        val names = listOf("Queen II", "The History Hour", "Queen", "The Rest Is History")
        assertEquals(listOf("Queen", "Queen II"), matchingSpoken("queen", names) { it })
        assertEquals(listOf("The History Hour", "The Rest Is History"), matchingSpoken("history", names) { it })
        assertEquals(emptyList<String>(), matchingSpoken("heat", names) { it })
    }

    @Test
    fun `reads music search results, and an older server has none`() {
        val older = FlexarrClient.json.decodeFromString(SearchResults.serializer(), """{"titles":[],"episodes":[],"people":[]}""")
        assertEquals(emptyList<SearchTrack>(), older.tracks)
        val newer = FlexarrClient.json.decodeFromString(
            SearchResults.serializer(),
            """{"titles":[],"episodes":[],"people":[],"genres":["Rock"],"albums":[],"tracks":[{"item":{"id":"t1","libraryId":"l","parentId":"a1","kind":"Track","title":"Bicycle Race","childCount":0,"artwork":{},"userData":{"positionSeconds":0,"watched":false,"playCount":0}},"album":"Jazz"}]}""",
        )
        assertEquals("a1", newer.tracks.single().item.parentId)
        assertEquals("Jazz", newer.tracks.single().album)
    }
}
