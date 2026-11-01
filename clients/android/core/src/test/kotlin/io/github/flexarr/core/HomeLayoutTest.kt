package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Home's New podcasts row and the audiobook shelves, placed by each profile's layout from 0.58 on. */
class HomeLayoutTest {
    private fun home(json: String): Home = FlexarrClient.json.decodeFromString(Home.serializer(), json)

    private fun shelves(json: String): BookShelves = FlexarrClient.json.decodeFromString(BookShelves.serializer(), json)

    private val item = """{"id":"a","libraryId":"l","kind":"Movie","title":"A","childCount":0,"artwork":{},"userData":{"positionSeconds":0,"watched":false,"playCount":0}}"""

    private fun ids(slots: List<HomeSlot>) = slots.map { if (it is HomeSlot.Row) it.row.id else "NEW" }

    @Test
    fun `a server that places New podcasts puts it where its empty row is`() {
        val home = home(
            """{"featured":[],"placesPodcasts":true,"rows":[
                {"id":"continue","title":"Continue watching","style":"wide","items":[$item]},
                {"id":"recent","title":"Latest","style":"poster","items":[$item]},
                {"id":"podcasts-new","title":"New podcasts","style":"podcasts","items":[],"link":"/podcasts"},
                {"id":"top","title":"Top 10","style":"top10","items":[$item]}
            ]}""",
        )
        assertEquals(true, home.placesPodcasts)
        assertEquals(HomeRowStyle.Podcasts, home.rows[2].style)
        assertTrue(home.rows[2].items.isEmpty())
        assertEquals(listOf("continue", "recent", "NEW", "top"), ids(home.slots { it.items.isNotEmpty() }))
    }

    @Test
    fun `switched off, a server that places it leaves New podcasts out`() {
        val home = home("""{"featured":[],"placesPodcasts":true,"rows":[{"id":"continue","title":"C","style":"wide","items":[$item]}]}""")
        assertEquals(listOf("continue"), ids(home.slots { it.items.isNotEmpty() }))
    }

    @Test
    fun `before 0_58 New podcasts follows Continue watching, or comes first`() {
        val older = home(
            """{"featured":[],"rows":[
                {"id":"continue","title":"C","style":"wide","items":[$item]},
                {"id":"empty","title":"E","style":"poster","items":[]},
                {"id":"recent","title":"R","style":"poster","items":[$item]}
            ]}""",
        )
        assertNull(older.placesPodcasts)
        assertEquals(listOf("continue", "NEW", "recent"), ids(older.slots { it.items.isNotEmpty() }))
        val noContinue = home("""{"featured":[],"rows":[{"id":"recent","title":"R","style":"poster","items":[$item]}]}""")
        assertEquals(listOf("NEW", "recent"), ids(noContinue.slots { it.items.isNotEmpty() }))
    }

    @Test
    fun `a row style this app doesn't know decodes as unknown`() {
        val home = home("""{"featured":[],"rows":[{"id":"x","title":"X","style":"somethingnew","items":[]}]}""")
        assertEquals(HomeRowStyle.Unknown, home.rows.single().style)
    }

    @Test
    fun `the audiobook front page follows the profile's order, without what it doesn't know`() {
        val ordered = shelves("""{"order":["recent","authors","later","continue"]}""")
        assertEquals(listOf("recent", "authors", "later", "continue"), ordered.order)
        assertEquals(listOf(BookShelf.RecentlyAdded, BookShelf.Authors, BookShelf.ContinueListening), ordered.frontPage)
        assertEquals(emptyList<BookShelf>(), shelves("""{"order":[]}""").frontPage)
    }

    @Test
    fun `before 0_58 the audiobook front page has every shelf in its usual order`() {
        val older = shelves("""{"continueListening":[],"recentlyAdded":[]}""")
        assertNull(older.order)
        assertEquals(
            listOf(BookShelf.ContinueListening, BookShelf.NextInSeries, BookShelf.RecentlyAdded, BookShelf.Series, BookShelf.Authors, BookShelf.Finished),
            older.frontPage,
        )
    }

    @Test
    fun `an audiobook front page with every shelf off, or only empty ones on, has nothing to show`() {
        val off = shelves("""{"order":[],"recentlyAdded":[$item]}""")
        assertTrue(!off.isEmpty && off.shownShelves.isEmpty())
        val emptyOnes = shelves("""{"order":["continue","finished"],"recentlyAdded":[$item]}""")
        assertTrue(emptyOnes.shownShelves.isEmpty())
        val some = shelves("""{"order":["finished","recent"],"recentlyAdded":[$item]}""")
        assertEquals(listOf(BookShelf.RecentlyAdded), some.shownShelves)
        assertTrue(shelves("""{"order":[]}""").isEmpty)
    }

    @Test
    fun `New podcasts episodes are fetched alongside Home until a Home has the row switched off`() {
        val memory = NewPodcastsMemory()
        // Nothing known yet: fetched.
        assertTrue(memory.fetchesAlongside("p"))
        val off = home("""{"featured":[],"placesPodcasts":true,"rows":[{"id":"recent","title":"Latest","style":"poster","items":[$item]}]}""")
        assertTrue(!off.hasNewPodcasts)
        memory.remember("p", off)
        assertTrue(!memory.fetchesAlongside("p"))
        // Another profile isn't affected.
        assertTrue(memory.fetchesAlongside("q"))
        // Switched back on: fetched alongside again from then on.
        val on = home("""{"featured":[],"placesPodcasts":true,"rows":[{"id":"podcasts-new","title":"New podcasts","style":"podcasts","items":[]}]}""")
        assertTrue(on.hasNewPodcasts)
        memory.remember("p", on)
        assertTrue(memory.fetchesAlongside("p"))
    }

    @Test
    fun `a server before 0_58 always has New podcasts, so the episodes are always fetched`() {
        val memory = NewPodcastsMemory()
        val older = home("""{"featured":[],"rows":[]}""")
        assertTrue(older.hasNewPodcasts)
        memory.remember(null, older)
        assertTrue(memory.fetchesAlongside(null))
    }
}
