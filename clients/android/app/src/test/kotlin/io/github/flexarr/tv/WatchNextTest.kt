package io.github.flexarr.tv

import androidx.tvprovider.media.tv.TvContractCompat
import io.github.flexarr.AppLink
import io.github.flexarr.core.Artwork
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.UserData
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** Which cards go in the TV home screen's Watch Next row, and how. */
class WatchNextTest {
    private val now = 1_800_000_000_000L

    private fun item(
        id: String,
        kind: MediaKind,
        position: Double = 0.0,
        watched: Boolean = false,
        playedAt: String? = null,
        series: String? = null,
    ) = ItemSummary(
        id = id, libraryId = "lib", kind = kind, title = "Title $id", childCount = 0, durationSeconds = 3600.0,
        seasonNumber = if (kind == MediaKind.Episode) 2 else null, episodeNumber = if (kind == MediaKind.Episode) 5 else null,
        artwork = Artwork(backdrop = "/api/v1/images/items/$id/backdrop?v=1"),
        userData = UserData(positionSeconds = position, watched = watched, playCount = 0, lastPlayedAt = playedAt),
        seriesTitle = series,
    )

    private fun cards(watching: List<ItemSummary>, albums: List<ItemSummary> = emptyList()) =
        WatchNext.cards(watching, albums, now) { "https://server/${it.id}.jpg" }

    @Test fun aFilmPartWayThrough_continuesWithItsProgress() {
        val card = cards(listOf(item("a", MediaKind.Movie, position = 900.0, playedAt = "2027-01-15T08:00:00Z"))).single()
        assertEquals(TvContractCompat.WatchNextPrograms.WATCH_NEXT_TYPE_CONTINUE, card.watchNextType)
        assertEquals(TvContractCompat.PreviewPrograms.TYPE_MOVIE, card.programType)
        assertEquals(900_000, card.positionMillis)
        assertEquals(3_600_000, card.durationMillis)
        assertEquals(java.time.Instant.parse("2027-01-15T08:00:00Z").toEpochMilli(), card.lastEngagement)
        assertEquals(AppLink.Play("a"), card.link)
        assertEquals("https://server/a.jpg", card.image)
    }

    @Test fun aFilmNotStarted_isLeftOut() {
        assertEquals(emptyList<WatchNext.Card>(), cards(listOf(item("a", MediaKind.Movie))))
    }

    @Test fun aShowsNextEpisode_isNext_underTheShowsName() {
        val card = cards(listOf(item("e", MediaKind.Episode, series = "Fargo"))).single()
        assertEquals(TvContractCompat.WatchNextPrograms.WATCH_NEXT_TYPE_NEXT, card.watchNextType)
        assertEquals("Fargo", card.title)
        assertEquals("Title e", card.episodeTitle)
        assertEquals(2, card.season)
        assertEquals(5, card.episode)
        assertNull(card.positionMillis)
    }

    @Test fun anEpisodePartWayThrough_continues() {
        val card = cards(listOf(item("e", MediaKind.Episode, position = 60.0, series = "Fargo"))).single()
        assertEquals(TvContractCompat.WatchNextPrograms.WATCH_NEXT_TYPE_CONTINUE, card.watchNextType)
    }

    @Test fun albums_comeAfterTheFilmsAndOpenTheirPage() {
        val row = cards(
            listOf(item("a", MediaKind.Movie, position = 10.0), item("b", MediaKind.Movie, position = 10.0)),
            albums = listOf(item("m", MediaKind.Album, series = "Artist"), item("t", MediaKind.Track)),
        )
        assertEquals(listOf("a", "b", "m"), row.map { it.id })
        assertEquals(AppLink.Album("m"), row.last().link)
        assertEquals(TvContractCompat.PreviewPrograms.TYPE_ALBUM, row.last().programType)
    }

    @Test fun theServersTimeWithoutAZone_isUtc() {
        assertEquals(
            java.time.Instant.parse("2027-01-15T08:00:00Z").toEpochMilli(),
            WatchNext.playedAt(item("a", MediaKind.Movie, playedAt = "2027-01-15T08:00:00")),
        )
    }
}
