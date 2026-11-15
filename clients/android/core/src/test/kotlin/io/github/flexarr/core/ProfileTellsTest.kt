package io.github.flexarr.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Two profiles on one phone: what the server is told about each one's downloads waits for a sign-in as that profile, and
 * a book kept by both is each one's own.
 */
class ProfileTellsTest {
    @Test
    fun `what's to be told of a download waits for a sign-in as its own profile`() {
        val entries = setOf(ProfileTells.entry("d1", "mum"), ProfileTells.entry("d2", "kid"), ProfileTells.entry("d3", "mum"))

        assertEquals(listOf("d1", "d3"), ProfileTells.due(entries, "mum").map { it.second }.sorted())
        assertEquals(listOf("d2"), ProfileTells.due(entries, "kid").map { it.second })
        // Nobody chosen (Who's watching): nothing is told.
        assertTrue(ProfileTells.due(entries, null).isEmpty())
        // Each comes back with its entry, to be let go once told.
        assertEquals(ProfileTells.entry("d2", "kid"), ProfileTells.due(entries, "kid").single().first)
    }

    @Test
    fun `entries kept before they said whose they were are told by whoever's signed in`() {
        val entries = setOf("old", ProfileTells.entry("d1", "kid"))

        assertEquals(listOf("old"), ProfileTells.due(entries, "mum").map { it.second })
        assertEquals(listOf("d1", "old"), ProfileTells.due(entries, "kid").map { it.second }.sorted())
        assertNull(ProfileTells.parse("old").first)
        assertEquals("kid" to "d1", ProfileTells.parse(ProfileTells.entry("d1", "kid")))
    }

    private fun book(profile: String, fileNames: List<String?>, image: String? = null) = LocalBook(
        itemId = "b1",
        profileId = profile,
        item = ItemSummary(id = "b1", libraryId = "books", kind = MediaKind.Audiobook, title = "Book", childCount = 0, artwork = Artwork(), userData = UserData(0.0, false, 0)),
        manifest = BookManifest("b1", 600.0, listOf(BookFile(0, "f0", 0.0, 300.0, url = "/a"), BookFile(1, "f1", 300.0, 300.0, url = "/b"))),
        fileNames = fileNames,
        imageFileName = image,
    )

    @Test
    fun `two profiles keeping one book each keep their own files`() {
        assertNotEquals(BookDownloadRules.fileName("b1", "mum", 0), BookDownloadRules.fileName("b1", "kid", 0))
        assertNotEquals(BookDownloadRules.imageName("b1", "mum"), BookDownloadRules.imageName("b1", "kid"))

        val mum = book("mum", listOf(BookDownloadRules.fileName("b1", "mum", 0), BookDownloadRules.fileName("b1", "mum", 1)), BookDownloadRules.imageName("b1", "mum"))
        val kid = book("kid", listOf(BookDownloadRules.fileName("b1", "kid", 0), null), BookDownloadRules.imageName("b1", "kid"))
        val going = BookDownloadRules.filesToDelete(kid, listOf(mum, kid))

        assertTrue(BookDownloadRules.fileName("b1", "kid", 0) in going)
        assertTrue(BookDownloadRules.fileName("b1", "kid", 1) + ".part" in going)
        assertTrue(BookDownloadRules.imageName("b1", "kid") in going)
        assertTrue(going.none { it in mum.fileNames || it == mum.imageFileName })
    }

    @Test
    fun `files two profiles shared from before stay while the other still holds them`() {
        // Kept by an app that named a book's files after the book alone: both entries name the same files.
        val shared = listOf("book-b1-0.audio", "book-b1-1.audio")
        val mum = book("mum", shared, "book-b1.jpg")
        val kid = book("kid", shared, "book-b1.jpg")

        val going = BookDownloadRules.filesToDelete(kid, listOf(mum, kid))
        assertTrue(going.none { it in shared || it == "book-b1.jpg" })
        assertTrue(going.none { it.startsWith("book-b1-") }) // nor the part files the other may be carrying on from
        // The last one to go takes them with it.
        val last = BookDownloadRules.filesToDelete(mum, listOf(mum))
        assertTrue(last.containsAll(shared + "book-b1.jpg"))
    }
}
