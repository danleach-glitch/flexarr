package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class BookAuthorsTest {
    private val itemJson =
        """{"id":"b1","libraryId":"l","kind":"Audiobook","title":"Killing Floor","childCount":0,"artwork":{},"userData":{"positionSeconds":0,"watched":false,"playCount":0}}"""

    private fun decode(book: String) = FlexarrClient.json.decodeFromString(ItemDetail.serializer(), """{"item":$itemJson,"book":$book}""")

    @Test
    fun `a book from a server before author cards has no authors and no cards`() {
        val book = decode("""{"durationSeconds":3600}""").book!!
        assertNull(book.authors)
        assertTrue(book.authorCards.isEmpty())
    }

    @Test
    fun `a book's authors decode in order, and only those with a photo or biography get a card`() {
        val book = decode(
            """{"authors":[
                {"id":"p1","name":"Lee Child","image":"/api/v1/images/people/p1?v=17","biography":"Born in Coventry.","source":"audible"},
                {"id":"p2","name":"Andrew Child","image":null,"biography":"  "},
                {"id":"p3","name":"Ann Other","biography":"Writes."}
            ]}""",
        ).book!!
        assertEquals(listOf("Lee Child", "Andrew Child", "Ann Other"), book.authors!!.map { it.name })
        assertEquals("/api/v1/images/people/p1?v=17", book.authors!![0].image)
        assertEquals("Born in Coventry.", book.authors!![0].biography)
        assertEquals(listOf("p1", "p3"), book.authorCards.map { it.id })
    }

    @Test
    fun `an author with only a name decodes, and gets no card`() {
        val book = decode("""{"authors":[{"id":"p1","name":"Lee Child"}]}""").book!!
        val author = book.authors!!.single()
        assertNull(author.image)
        assertNull(author.biography)
        assertTrue(book.authorCards.isEmpty())
    }
}
