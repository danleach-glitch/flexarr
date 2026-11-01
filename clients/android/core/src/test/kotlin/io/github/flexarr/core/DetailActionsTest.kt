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
    fun `a part-watched movie can start over, straight after Play`() {
        assertEquals(listOf(PLAY, START_OVER, TRAILER, WATCHED, WATCHLIST), actions(hasProgress = true).buttons)
    }

    @Test
    fun `a watched movie undoes it with Watched, not the menu`() {
        val result = actions(isWatched = true)
        assertEquals(listOf(PLAY, TRAILER, WATCHED, WATCHLIST), result.buttons)
        assertEquals(emptyList<DetailActions.MenuItem>(), result.menu)
    }

    @Test
    fun `a show shuffles`() {
        assertEquals(listOf(PLAY, SHUFFLE, TRAILER, WATCHED, WATCHLIST), actions(kind = Kind.SHOW).buttons)
        assertEquals(listOf(PLAY, START_OVER, SHUFFLE, TRAILER, WATCHED, WATCHLIST), actions(kind = Kind.SHOW, hasProgress = true).buttons)
    }

    @Test
    fun `an episode goes to its show from the menu, and has no watchlist`() {
        val result = actions(kind = Kind.EPISODE, hasTrailer = false)
        assertEquals(listOf(PLAY, WATCHED, MORE), result.buttons)
        assertEquals(listOf(GO_TO_SHOW), result.menu)
    }

    @Test
    fun `subtitles go in the menu`() {
        val result = actions(canSearchSubtitles = true)
        assertEquals(listOf(PLAY, TRAILER, WATCHED, WATCHLIST, MORE), result.buttons)
        assertEquals(listOf(SUBTITLES), result.menu)
        assertEquals(listOf(SUBTITLES, GO_TO_SHOW), actions(kind = Kind.EPISODE, canSearchSubtitles = true).menu)
    }

    @Test
    fun `a child gets no trailer, watched, watchlist or subtitles`() {
        val result = actions(isKids = true, canSearchSubtitles = true, hasProgress = true)
        assertEquals(listOf(PLAY, START_OVER), result.buttons)
        assertEquals(emptyList<DetailActions.MenuItem>(), result.menu)
        assertEquals(listOf(PLAY, SHUFFLE), actions(kind = Kind.SHOW, isKids = true).buttons)
    }

    @Test
    fun `a child's watched title can be marked unwatched from the menu, as there's no Watched button`() {
        val result = actions(isKids = true, isWatched = true)
        assertEquals(listOf(PLAY, MORE), result.buttons)
        assertEquals(listOf(MARK_UNWATCHED), result.menu)
    }

    @Test
    fun `a TV without the YouTube app has no trailer`() {
        assertEquals(listOf(PLAY, WATCHED, WATCHLIST), actions(canPlayTrailers = false).buttons)
    }

    @Test
    fun `no trailer id, no trailer`() {
        assertEquals(listOf(PLAY, WATCHED, WATCHLIST), actions(hasTrailer = false).buttons)
    }

    @Test
    fun `only a phone downloads`() {
        assertEquals(listOf(PLAY, TRAILER, WATCHED, WATCHLIST, DOWNLOAD), actions(platform = Platform.PHONE, canDownload = true).buttons)
        assertEquals(listOf(PLAY, TRAILER, WATCHED, WATCHLIST), actions(platform = Platform.PHONE).buttons)
        assertEquals(listOf(PLAY, TRAILER, WATCHED, WATCHLIST), actions(platform = Platform.TV, canDownload = true).buttons)
    }

    @Test
    fun `a server without a watchlist has no Watchlist button`() {
        val result = DetailActions.of(DetailActions.Input(Kind.MOVIE, false, false, false, false, Platform.PHONE, hasWatchlist = false))
        assertEquals(listOf(PLAY, WATCHED), result.buttons)
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
