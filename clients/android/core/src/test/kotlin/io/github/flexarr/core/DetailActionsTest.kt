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
import io.github.flexarr.core.DetailActions.MenuItem.GoToShow
import io.github.flexarr.core.DetailActions.MenuItem.Moved
import io.github.flexarr.core.DetailActions.MenuItem.Subtitles
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
        canWatchlist: Boolean = true,
        canChooseVersion: Boolean = false,
    ) = DetailActions.of(DetailActions.Input(kind, hasProgress, isWatched, hasTrailer, isKids, platform, canPlayTrailers, canDownload, canSearchSubtitles, canWatchlist, canChooseVersion))

    @Test
    fun `versions come before subtitles, for a child too, and only with something to choose`() {
        assertEquals(listOf(DetailActions.MenuItem.Version, DetailActions.MenuItem.Subtitles), actions(canChooseVersion = true, canSearchSubtitles = true).menu)
        assertEquals(listOf(DetailActions.MenuItem.Version), actions(canChooseVersion = true, isKids = true).menu)
        assertEquals(emptyList<DetailActions.MenuItem>(), actions().menu)
    }

    @Test
    fun `a version's name gives its cut and length only where they differ`() {
        val same = listOf(TitleVersion("a", "4K", durationSeconds = 7200.0), TitleVersion("b", "1080p", durationSeconds = 7180.0))
        assertEquals("4K", same[0].name(same))
        val cuts = listOf(TitleVersion("a", "4K", "Director's Cut", 10800.0), TitleVersion("b", "1080p", durationSeconds = 7200.0))
        assertEquals("4K, Director's Cut, 3h", cuts[0].name(cuts))
        assertEquals("1080p, 2h", cuts[1].name(cuts))
    }

    @Test
    fun `a fresh movie plays, has its trailer, watched and watchlist`() {
        val result = actions()
        assertEquals(listOf(PLAY, TRAILER, WATCHED, WATCHLIST), result.buttons)
        assertEquals(emptyList<DetailActions.MenuItem>(), result.menu)
    }

    @Test
    fun `a part-watched movie can start over`() {
        val result = actions(hasProgress = true)
        assertEquals(listOf(PLAY, START_OVER, TRAILER, WATCHED, WATCHLIST), result.buttons)
        assertEquals(emptyList<DetailActions.MenuItem>(), result.menu)
    }

    @Test
    fun `a watched movie unmarks with its own button`() {
        val result = actions(isWatched = true)
        assertEquals(listOf(PLAY, TRAILER, WATCHED, WATCHLIST), result.buttons)
        assertEquals(emptyList<DetailActions.MenuItem>(), result.menu)
    }

    // A phone's row holds three beside Play, like an episode's; the rest go to the top of ⋯.
    @Test
    fun `a phone before starting keeps trailer and watchlist`() {
        val result = actions(platform = Platform.PHONE, canDownload = true)
        assertEquals(listOf(PLAY, TRAILER, WATCHLIST, DOWNLOAD, MORE), result.buttons)
        assertEquals(listOf(Moved(WATCHED)), result.menu)
    }

    @Test
    fun `a phone part way keeps start over and watched`() {
        val result = actions(platform = Platform.PHONE, hasProgress = true, canDownload = true, canSearchSubtitles = true)
        assertEquals(listOf(PLAY, START_OVER, WATCHED, DOWNLOAD, MORE), result.buttons)
        assertEquals(listOf(Moved(TRAILER), Moved(WATCHLIST), Subtitles), result.menu)
    }

    @Test
    fun `a phone once watched keeps watched and watchlist`() {
        val result = actions(platform = Platform.PHONE, isWatched = true, canDownload = true)
        assertEquals(listOf(PLAY, WATCHED, WATCHLIST, DOWNLOAD, MORE), result.buttons)
        assertEquals(listOf(Moved(TRAILER)), result.menu)
    }

    @Test
    fun `a phone show shuffles from the menu`() {
        val result = actions(kind = Kind.SHOW, platform = Platform.PHONE)
        assertEquals(listOf(PLAY, TRAILER, WATCHED, WATCHLIST, MORE), result.buttons)
        assertEquals(listOf(Moved(SHUFFLE)), result.menu)
    }

    @Test
    fun `a phone episode matches its page`() {
        val result = actions(kind = Kind.EPISODE, platform = Platform.PHONE, hasProgress = true, hasTrailer = false, canDownload = true, canWatchlist = false)
        assertEquals(listOf(PLAY, START_OVER, WATCHED, DOWNLOAD, MORE), result.buttons)
        assertEquals(listOf(GoToShow), result.menu)
    }

    @Test
    fun `a phone without a trailer fills the row with watched`() {
        assertEquals(listOf(PLAY, WATCHED, WATCHLIST, DOWNLOAD), actions(platform = Platform.PHONE, hasTrailer = false, canDownload = true).buttons)
    }

    @Test
    fun `a show shuffles`() {
        assertEquals(listOf(PLAY, SHUFFLE, TRAILER, WATCHED, WATCHLIST), actions(kind = Kind.SHOW).buttons)
        assertFalse(SHUFFLE in actions().buttons)
        assertFalse(SHUFFLE in actions(kind = Kind.EPISODE).buttons)
    }

    @Test
    fun `an episode goes to its show from the menu`() {
        val result = actions(kind = Kind.EPISODE, hasTrailer = false, canWatchlist = false)
        assertEquals(listOf(PLAY, WATCHED, MORE), result.buttons)
        assertEquals(listOf(GoToShow), result.menu)
    }

    @Test
    fun `a child gets no trailer, watched, watchlist or subtitles`() {
        val result = actions(isKids = true, canSearchSubtitles = true, hasProgress = true)
        assertEquals(listOf(PLAY, START_OVER), result.buttons)
        assertEquals(emptyList<DetailActions.MenuItem>(), result.menu)
        assertEquals(listOf(PLAY, SHUFFLE), actions(kind = Kind.SHOW, isKids = true).buttons)
        assertEquals(listOf(GoToShow), actions(kind = Kind.EPISODE, isKids = true).menu)
    }

    @Test
    fun `a TV without the YouTube app has no trailer`() {
        assertFalse(TRAILER in actions(platform = Platform.TV, canPlayTrailers = false).buttons)
        assertTrue(TRAILER in actions(platform = Platform.TV, canPlayTrailers = true).buttons)
        assertFalse(TRAILER in actions(hasTrailer = false).buttons)
    }

    @Test
    fun `phones download, TVs don't`() {
        assertTrue(DOWNLOAD in actions(platform = Platform.PHONE, canDownload = true).buttons)
        assertFalse(DOWNLOAD in actions(platform = Platform.TV, canDownload = true).buttons)
        assertFalse(DOWNLOAD in actions(platform = Platform.PHONE, canDownload = false).buttons)
    }

    @Test
    fun `the menu keeps its order`() {
        val result = actions(kind = Kind.SHOW, platform = Platform.PHONE, hasProgress = true, canSearchSubtitles = true)
        assertEquals(listOf(Moved(SHUFFLE), Moved(TRAILER), Subtitles), result.menu)
        assertEquals(listOf(Subtitles, GoToShow), actions(kind = Kind.EPISODE, canSearchSubtitles = true).menu)
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
