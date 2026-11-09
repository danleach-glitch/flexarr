package io.github.flexarr.tv

import androidx.tvprovider.media.tv.TvContractCompat
import io.github.flexarr.AppLink
import io.github.flexarr.core.Artwork
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.Profile
import io.github.flexarr.core.UserData
import org.junit.Assert.assertEquals
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
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
        WatchNext.cards(watching, albums, now) { WatchNext.Picture("https://server/${it.id}.jpg", 0) }

    @Test fun aFilmPartWayThrough_continuesWithItsProgress() {
        val card = cards(listOf(item("a", MediaKind.Movie, position = 900.0, playedAt = "2027-01-15T08:00:00Z"))).single()
        assertEquals(TvContractCompat.WatchNextPrograms.WATCH_NEXT_TYPE_CONTINUE, card.watchNextType)
        assertEquals(TvContractCompat.PreviewPrograms.TYPE_MOVIE, card.programType)
        assertEquals(900_000, card.positionMillis)
        assertEquals(3_600_000, card.durationMillis)
        assertEquals(java.time.Instant.parse("2027-01-15T08:00:00Z").toEpochMilli(), card.lastEngagement)
        assertEquals(AppLink.Play("a"), card.link)
        assertEquals("https://server/a.jpg", card.image?.url)
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

    @Test fun aNextEpisodeWithoutATime_keepsTheServersOrder() {
        // The server puts a film paused yesterday before a show last watched last week, whose next episode was never played.
        val row = cards(listOf(
            item("film", MediaKind.Movie, position = 600.0, playedAt = "2027-01-14T08:00:00Z"),
            item("next", MediaKind.Episode, series = "Show"),
            item("older", MediaKind.Movie, position = 60.0, playedAt = "2027-01-01T08:00:00Z"),
        ))
        assertEquals(listOf("film", "next", "older"), row.map { it.id })
        assertEquals(java.time.Instant.parse("2027-01-14T08:00:00Z").toEpochMilli(), row[0].lastEngagement)
    }

    @Test fun anAlbum_hasItsArtistAsTheAuthor() {
        val card = cards(emptyList(), albums = listOf(item("m", MediaKind.Album, series = "Artist", playedAt = "2027-01-15T08:00:00Z"))).single()
        assertEquals("Artist", card.author)
        assertNull(card.episodeTitle)
        assertTrue(card.played)
    }

    @Test fun madeUpTimes_stayTheSameFromOneRefreshToTheNext() {
        val watching = listOf(item("next", MediaKind.Episode, series = "Show"), item("film", MediaKind.Movie, position = 600.0, playedAt = "2027-01-14T08:00:00Z"))
        val first = WatchNext.cards(watching, emptyList(), now) { null }
        val later = WatchNext.cards(watching, emptyList(), now + 3_600_000) { null }
        assertEquals(first, later)
        assertEquals(listOf("next", "film"), first.map { it.id })
        assertFalse(first[0].played)
        assertTrue(first[1].played)
    }

    private fun profile(id: String, current: Boolean = false, main: Boolean = false, pin: Boolean = false) =
        Profile(id = id, name = id, color = "#fff", hasPin = pin, isMain = main, isCurrent = current)

    @Test fun theChosenProfile_isWhoTheRowIsFor() {
        val profiles = listOf(profile("main", main = true), profile("mum", current = true))
        assertEquals("mum", WatchNext.watching(profiles, "mum.sig")?.id)
    }

    @Test fun aPinAddedToTheChosenProfile_emptiesTheRow() {
        assertNull(WatchNext.watching(listOf(profile("main", main = true), profile("mum", current = true, pin = true)), "mum.sig"))
    }

    @Test fun aRetiredTokenAnsweredAsTheMainProfile_emptiesTheRow() {
        // Mum's PIN changed her token's signature: a server-key sign-in then acts as the main profile, which has no PIN.
        val profiles = listOf(profile("main", main = true, current = true), profile("mum", pin = true))
        assertNull(WatchNext.watching(profiles, "mum.sig"))
    }

    @Test fun nobodyCurrent_emptiesTheRow() {
        assertNull(WatchNext.watching(listOf(profile("main", main = true), profile("mum")), "mum.sig"))
    }

    @Test fun nobodyChosen_oneProfile_isTheMainOne() {
        assertEquals("main", WatchNext.watching(listOf(profile("main", main = true, current = true)), null)?.id)
    }

    @Test fun nobodyChosen_severalProfiles_emptiesTheRow() {
        assertNull(WatchNext.watching(listOf(profile("main", main = true, current = true), profile("mum")), null))
    }

    @Test fun aRefusal_emptiesTheRow_butBeingOfflineKeepsIt() {
        assertTrue(WatchNext.isRefused(FlexarrException.Unauthorized()))
        assertTrue(WatchNext.isRefused(FlexarrException.KidsLocked(null)))
        assertFalse(WatchNext.isRefused(FlexarrException.Http(500, null)))
        assertFalse(WatchNext.isRefused(FlexarrException.Unreachable(java.io.IOException())))
    }

    /** What the real client makes of an answer to the row's own question. */
    private fun answering(code: Int, type: String, body: String): FlexarrException {
        val http = okhttp3.OkHttpClient.Builder().addInterceptor { chain ->
            okhttp3.Response.Builder().request(chain.request()).protocol(okhttp3.Protocol.HTTP_1_1).code(code).message("")
                .body(body.toResponseBody(type.toMediaType())).build()
        }.build()
        val client = io.github.flexarr.core.FlexarrClient("http://flexarr.test/".toHttpUrl(), apiKey = "key", http = http)
        return try {
            kotlinx.coroutines.runBlocking { client.continueWatching(take = 1) }
            throw AssertionError("It should have failed")
        } catch (e: FlexarrException) {
            e
        }
    }

    @Test fun aProxysBare403_keepsTheRow() {
        assertFalse(WatchNext.isRefused(answering(403, "text/html", "<html>403 Forbidden</html>")))
    }

    @Test fun theServersOwn403s_emptyTheRow() {
        fun problem(flag: String) = answering(403, "application/problem+json", """{"title":"No","$flag":true}""")
        assertTrue(WatchNext.isRefused(problem("guestRemoved")))
        assertTrue(WatchNext.isRefused(problem("chooseProfile")))
        assertTrue(WatchNext.isRefused(problem("kidsLock")))
        assertTrue(WatchNext.isRefused(problem("parentPin")))
        assertTrue(WatchNext.isRefused(answering(401, "application/json", "{}")))
    }
}
