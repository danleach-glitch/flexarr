package io.github.flexarr.core

import io.github.flexarr.core.DetailActions.Button.DOWNLOAD
import io.github.flexarr.core.DetailActions.Button.MORE
import io.github.flexarr.core.DetailActions.Button.PLAY
import io.github.flexarr.core.DetailActions.Button.SHUFFLE
import io.github.flexarr.core.DetailActions.Button.START_OVER
import io.github.flexarr.core.DetailActions.Button.TRAILER
import io.github.flexarr.core.DetailActions.Button.WATCHED
import io.github.flexarr.core.DetailActions.Button.WATCHLIST
import io.github.flexarr.core.DetailActions.Kind
import io.github.flexarr.core.DetailActions.MenuItem.GO_TO_SHOW
import io.github.flexarr.core.DetailActions.MenuItem.MARK_UNWATCHED
import io.github.flexarr.core.DetailActions.MenuItem.SUBTITLES
import io.github.flexarr.core.DetailActions.Platform
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The title page's actions and their order, as FlexarrKit's DetailActionsTests. */
class DetailActionsTest {
    private fun actions(
        kind: Kind = Kind.MOVIE,
        hasProgress: Boolean = false,
        isWatched: Boolean = false,
        hasTrailer: Boolean = true,
        isKids: Boolean = false,
        platform: Platform = Platform.TV,
        canPlayTrailers: Boolean = true,
        canDownload: Boolean = false,
        canSearchSubtitles: Boolean = false,
    ) = DetailActions.of(DetailActions.Input(kind, hasProgress, isWatched, hasTrailer, isKids, platform, canPlayTrailers, canDownload, canSearchSubtitles))

    @Test
    fun `a fresh movie plays, has its trailer, watched and watchlist`() {
        val result = actions()
        assertEquals(listOf(PLAY, TRAILER, WATCHED, WATCHLIST), result.buttons)
        assertEquals(emptyList<DetailActions.MenuItem>(), result.menu)
    }

    @Test
    fun `a part-watched movie can start over, or be forgotten from the menu`() {
        val result = actions(hasProgress = true)
        assertEquals(listOf(PLAY, START_OVER, TRAILER, WATCHED, WATCHLIST, MORE), result.buttons)
        assertEquals(listOf(MARK_UNWATCHED), result.menu)
    }

    @Test
    fun `a watched movie unmarks with its own button, not the menu`() {
        val result = actions(isWatched = true)
        assertEquals(listOf(PLAY, TRAILER, WATCHED, WATCHLIST), result.buttons)
        assertEquals(emptyList<DetailActions.MenuItem>(), result.menu)
        // Rewatching part way: still only the one Mark as unwatched, the Watched button's.
        assertEquals(emptyList<DetailActions.MenuItem>(), actions(hasProgress = true, isWatched = true).menu)
    }

    @Test
    fun `a show shuffles`() {
        assertEquals(listOf(PLAY, SHUFFLE, TRAILER, WATCHED, WATCHLIST), actions(kind = Kind.SHOW).buttons)
        assertFalse(SHUFFLE in actions().buttons)
        assertFalse(SHUFFLE in actions(kind = Kind.EPISODE).buttons)
    }

    @Test
    fun `an episode goes to its show from the menu`() {
        val result = actions(kind = Kind.EPISODE, hasTrailer = false)
        assertEquals(listOf(PLAY, WATCHED, WATCHLIST, MORE), result.buttons)
        assertEquals(listOf(GO_TO_SHOW), result.menu)
    }

    @Test
    fun `a child gets no trailer, watched, watchlist or subtitles`() {
        val result = actions(isKids = true, canSearchSubtitles = true, hasProgress = true)
        assertEquals(listOf(PLAY, START_OVER), result.buttons)
        assertEquals(emptyList<DetailActions.MenuItem>(), result.menu)
        assertEquals(listOf(PLAY, SHUFFLE), actions(kind = Kind.SHOW, isKids = true).buttons)
        assertEquals(listOf(GO_TO_SHOW), actions(kind = Kind.EPISODE, isKids = true).menu)
    }

    @Test
    fun `a TV without the YouTube app has no trailer`() {
        assertFalse(TRAILER in actions(platform = Platform.TV, canPlayTrailers = false).buttons)
        assertTrue(TRAILER in actions(platform = Platform.TV, canPlayTrailers = true).buttons)
        assertFalse(TRAILER in actions(hasTrailer = false).buttons)
    }

    @Test
    fun `phones download, TVs don't`() {
        assertEquals(listOf(PLAY, TRAILER, WATCHED, WATCHLIST, DOWNLOAD), actions(platform = Platform.PHONE, canDownload = true).buttons)
        assertFalse(DOWNLOAD in actions(platform = Platform.TV, canDownload = true).buttons)
        assertFalse(DOWNLOAD in actions(platform = Platform.PHONE, canDownload = false).buttons)
    }

    @Test
    fun `the menu keeps its order`() {
        val result = actions(kind = Kind.EPISODE, hasProgress = true, canSearchSubtitles = true)
        assertEquals(listOf(MARK_UNWATCHED, SUBTITLES, GO_TO_SHOW), result.menu)
        assertEquals(MORE, result.buttons.last())
    }

    @Test
    fun `only a real YouTube id is a trailer`() {
        assertEquals("nelkiuezJxg", youTubeId("nelkiuezJxg"))
        assertEquals("a-b_C123456", youTubeId("a-b_C123456"))
        assertEquals(null, youTubeId(null))
        assertEquals(null, youTubeId("short"))
        assertEquals(null, youTubeId("nelkiuezJxg\"><script>"))
        assertEquals(null, youTubeId("abc/def?x=1"))
    }

    @Test
    fun `an item's trailer decodes, and is absent from older servers`() {
        val item = """{"id":"m1","libraryId":"l","kind":"Movie","title":"The Invite","childCount":0,"artwork":{},"userData":{"positionSeconds":0,"watched":false,"playCount":0}}"""
        val withTrailer = FlexarrClient.json.decodeFromString(ItemDetail.serializer(), """{"item":$item,"trailerYouTubeId":"nelkiuezJxg"}""")
        assertEquals("nelkiuezJxg", withTrailer.trailerId)
        val older = FlexarrClient.json.decodeFromString(ItemDetail.serializer(), """{"item":$item}""")
        assertEquals(null, older.trailerId)
        val junk = FlexarrClient.json.decodeFromString(ItemDetail.serializer(), """{"item":$item,"trailerYouTubeId":"not an id"}""")
        assertEquals(null, junk.trailerId)
    }
}
