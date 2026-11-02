package io.github.flexarr

import android.app.Application
import android.net.Uri
import io.github.flexarr.core.LibrarySort
import io.github.flexarr.core.LibraryType
import io.github.flexarr.ui.LibraryLink
import io.github.flexarr.ui.library.hasBrowse
import io.github.flexarr.ui.library.opensOnBrowse
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/** Which of a library's views a link opens: the plain library its Browse, a row's See all its titles that way. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class LibraryLinkTest {
    private fun opens(link: String): String? = LibraryLink.parse(Uri.parse(link))?.let { if (opensOnBrowse(it.sort, it.genre)) "browse" else "all" }

    @Test
    fun `the plain library opens on Browse`() {
        assertEquals("browse", opens("/library/abc"))
    }

    @Test
    fun `a Browse row's See all opens the titles sorted or in its genre`() {
        assertEquals(LibraryLink("abc", LibrarySort.Added, null), LibraryLink.parse(Uri.parse("/library/abc?view=all&sort=added")))
        assertEquals(LibraryLink("abc", LibrarySort.Title, "Science Fiction"), LibraryLink.parse(Uri.parse("/library/abc?view=all&genre=Science%20Fiction")))
        assertEquals("all", opens("/library/abc?view=all&sort=year"))
        assertEquals("all", opens("/library/abc?view=all&genre=Drama"))
        // Home's rows, as before.
        assertEquals("all", opens("/library/abc?sort=added"))
    }

    @Test
    fun `other links aren't a library`() {
        assertNull(LibraryLink.parse(Uri.parse("/watchlist")))
        assertNull(LibraryLink.parse(Uri.parse("/library")))
    }

    @Test
    fun `films, shows and home videos have Browse, the rest their own screens`() {
        assertTrue(hasBrowse(LibraryType.Movies))
        assertTrue(hasBrowse(LibraryType.TvShows))
        assertTrue(hasBrowse(LibraryType.HomeVideos))
        assertFalse(hasBrowse(LibraryType.Music))
        assertFalse(hasBrowse(LibraryType.Audiobooks))
        assertFalse(hasBrowse(LibraryType.Podcasts))
    }
}
