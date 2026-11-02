package io.github.flexarr.core

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import kotlinx.serialization.Serializable

// What a show or a book series has that the library doesn't, as FlexarrKit's ShowMissing.swift: a show's missing seasons
// and episodes (from Sonarr, or Seerr's seasons), the episodes coming next, and a book series' books Audible lists.
// All of it is absent from servers before 0.59, which answer 404: then there's nothing to show.

/** A season of a show the library has none of. */
@Serializable
data class MissingSeason(
    val seasonNumber: Int,
    val name: String? = null,
    /** How many of its episodes have aired. */
    val episodes: Int = 0,
    val year: Int? = null,
    val poster: String? = null,
    /** Requested when Sonarr is already looking for it, or Seerr says so. */
    val availability: Availability = Availability.Unknown,
) {
    val title: String get() = name?.takeIf { it.isNotBlank() } ?: "Season $seasonNumber"
    val requested: Boolean get() = availability == Availability.Requested || availability == Availability.PartlyAvailable
    val detail: String get() = listOfNotNull(if (episodes > 0) episodesLabel(episodes) else null, year?.toString()).joinToString(", ")
}

/** An episode: one that aired and the library lacks, or one still to air. */
@Serializable
data class ShowEpisode(
    val seasonNumber: Int,
    val episodeNumber: Int,
    val title: String? = null,
    @Serializable(with = InstantSerializer::class) val airDate: Instant? = null,
    val still: String? = null,
    /** Sonarr is looking for it. */
    val monitored: Boolean = false,
) {
    val code: String get() = "S${seasonNumber}E$episodeNumber"
    val label: String get() = title?.let { "$code · $it" } ?: code
}

/** Of a season the library has: how many of its aired episodes it has. */
@Serializable
data class SeasonCount(val seasonNumber: Int, val have: Int, val aired: Int) {
    /** "8 of 10" when some are missing; null when it has them all. */
    val label: String? get() = if (have < aired) "$have of $aired" else null
}

/** What a show has that the library doesn't, and what's coming. */
@Serializable
data class ShowMissing(
    /** "sonarr" (seasons and episodes), "seerr" (whole seasons only) or "none". */
    val source: String = "none",
    val canRequest: Boolean = false,
    /** The main profile, for a show Sonarr manages: Search asks Sonarr for missing episodes. */
    val canSearch: Boolean = false,
    val seasons: List<MissingSeason> = emptyList(),
    val episodes: List<ShowEpisode> = emptyList(),
    val upcoming: List<ShowEpisode> = emptyList(),
    val counts: List<SeasonCount> = emptyList(),
) {
    val isEmpty: Boolean get() = seasons.isEmpty() && episodes.isEmpty()

    /** "8 of 10" for a season missing some of its aired episodes. */
    fun countLabel(seasonNumber: Int?): String? = counts.firstOrNull { it.seasonNumber == seasonNumber }?.label

    /** The missing episodes, a season at a time. */
    val episodesBySeason: List<Pair<Int, List<ShowEpisode>>>
        get() = episodes.groupBy { it.seasonNumber }.toSortedMap().map { (season, list) -> season to list }

    companion object {
        val None = ShowMissing()

        /** "Airs 12 Nov", or "Airs 12 Nov 2027" when it isn't this year. */
        fun airsLabel(date: Instant, zone: ZoneId = ZoneId.systemDefault(), now: Instant = Instant.now()): String = "Airs ${airedLabel(date, zone, now)}"

        /** "12 Nov", or "12 Nov 2021" when it isn't this year: when a missing episode aired. */
        fun airedLabel(date: Instant, zone: ZoneId = ZoneId.systemDefault(), now: Instant = Instant.now()): String {
            val day = date.atZone(zone).toLocalDate()
            val pattern = if (day.year == now.atZone(zone).year) "d MMM" else "d MMM yyyy"
            return day.format(DateTimeFormatter.ofPattern(pattern, Locale.UK))
        }
    }
}

/** A book of a series that the library doesn't have, as Audible lists it. */
@Serializable
data class MissingBook(
    val asin: String,
    val title: String,
    /** Its number in the series as written ("3", "1.5"). */
    val sequence: String? = null,
    val authors: List<String> = emptyList(),
    val year: Int? = null,
    val cover: String? = null,
) {
    /** "Book 3, 2021". */
    val detail: String get() = listOfNotNull(sequence?.let { "Book $it" }, year?.toString()).joinToString(", ")
}

@Serializable
data class BookSeriesMissing(val books: List<MissingBook> = emptyList(), val answered: Boolean = false)

@Serializable
internal data class SeasonsRequest(val mediaType: String, val tmdbId: Int, val seasons: List<Int>)

@Serializable
internal data class MissingSearchRequest(val seasonNumber: Int, val episodeNumbers: List<Int>? = null)

private fun episodesLabel(count: Int) = if (count == 1) "1 episode" else "$count episodes"

/** What a library show lacks; [ShowMissing.None] from servers before 0.59. */
suspend fun FlexarrClient.showMissing(showId: String): ShowMissing =
    try {
        get("items/$showId/missing")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) ShowMissing.None else throw e
    }

/** Asks Seerr for just these seasons of a show, by its TMDB id. */
suspend fun FlexarrClient.requestSeasons(tmdbId: Int, seasons: List<Int>) {
    send("POST", "requests", body = FlexarrClient.json.encodeToString(SeasonsRequest.serializer(), SeasonsRequest("tv", tmdbId, seasons)))
}

/** Asks Sonarr to look for a season's missing episodes (just [episodes], or all of them). Main profile only. */
suspend fun FlexarrClient.searchMissing(showId: String, seasonNumber: Int, episodes: List<Int>? = null) {
    send("POST", "items/$showId/missing/search", body = FlexarrClient.json.encodeToString(MissingSearchRequest.serializer(), MissingSearchRequest(seasonNumber, episodes)))
}

/** The books Audible lists in a series that the library lacks; none from servers before 0.59. */
suspend fun FlexarrClient.bookSeriesMissing(seriesId: String): BookSeriesMissing =
    try {
        get("book-series/$seriesId/missing")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) BookSeriesMissing() else throw e
    }
