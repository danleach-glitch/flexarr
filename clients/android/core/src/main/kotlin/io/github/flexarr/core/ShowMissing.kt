package io.github.flexarr.core

import io.github.flexarr.core.i18n.Words
import io.github.flexarr.core.i18n.t
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
    val title: String get() = name?.takeIf { it.isNotBlank() } ?: t("Season {season}", "season" to seasonNumber.toString())
    val requested: Boolean get() = availability == Availability.Requested || availability == Availability.PartlyAvailable
    val detail: String get() = listOfNotNull(if (episodes > 0) episodesLabel(episodes) else null, year?.toString()).joinToString(", ")
}

/** A card in a show's *Not in your library* row: a season the library has none of, or one it has part of. */
sealed interface SeasonGap {
    val seasonNumber: Int

    data class Whole(val season: MissingSeason) : SeasonGap {
        override val seasonNumber: Int get() = season.seasonNumber
    }

    /** A season the library has some of, and how many of its aired episodes it lacks. */
    data class Part(override val seasonNumber: Int, val missing: Int) : SeasonGap {
        val title: String get() = t("Season {season}", "season" to seasonNumber.toString())
        val detail: String get() = t("{count, plural, one {# episode missing} other {# episodes missing}}", "count" to missing)
    }
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
    val code: String get() = t(Words.seasonEpisodeShort, "season" to seasonNumber.toString(), "episode" to episodeNumber.toString())
    val label: String get() = title?.let { "$code · $it" } ?: code
}

/** Of a season the library has: how many of its aired episodes it has. */
@Serializable
data class SeasonCount(val seasonNumber: Int, val have: Int, val aired: Int) {
    /** "8 of 10" when some are missing; null when it has them all. */
    val label: String? get() = if (have < aired) t("{have} of {count}", "have" to have, "count" to aired) else null
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

    /** *Not in your library*'s one row: the seasons it has none of and those it has part of, by season number. */
    val seasonGaps: List<SeasonGap>
        get() = (seasons.map { SeasonGap.Whole(it) } + episodesBySeason.map { (season, list) -> SeasonGap.Part(season, list.size) })
            .sortedBy { it.seasonNumber }

    /** Whether Search can ask Sonarr for a partly-had season's missing episodes (the main profile, a Sonarr show). */
    val canSearchEpisodes: Boolean get() = canSearch && source == "sonarr"

    companion object {
        val None = ShowMissing()

        /** "Airs 12 Nov", or "Airs 12 Nov 2027" when it isn't this year. */
        fun airsLabel(date: Instant, zone: ZoneId = ZoneId.systemDefault(), now: Instant = Instant.now()): String = t("Airs {day}", "day" to airedLabel(date, zone, now))

        /** "12 Nov", or "12 Nov 2021" when it isn't this year: when a missing episode aired. */
        fun airedLabel(date: Instant, zone: ZoneId = ZoneId.systemDefault(), now: Instant = Instant.now()): String {
            val day = date.atZone(zone).toLocalDate()
            val pattern = if (day.year == now.atZone(zone).year) "d MMM" else "d MMM yyyy"
            return day.format(DateTimeFormatter.ofPattern(pattern, io.github.flexarr.core.i18n.Localization.locale))
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
    val detail: String get() = listOfNotNull(sequence?.let { t("Book {number}", "number" to it) }, year?.toString()).joinToString(", ")
}

@Serializable
data class BookSeriesMissing(val books: List<MissingBook> = emptyList(), val answered: Boolean = false)

@Serializable
internal data class SeasonsRequest(val mediaType: String, val tmdbId: Int, val seasons: List<Int>)

@Serializable
internal data class MissingSearchRequest(val seasonNumber: Int, val episodeNumbers: List<Int>? = null)

private fun episodesLabel(count: Int) = t("{count, plural, one {# episode} other {# episodes}}", "count" to count)

/** What a library show lacks; [ShowMissing.None] from servers before 0.59 (a 404, or their web page). */
suspend fun FlexarrClient.showMissing(showId: String): ShowMissing =
    try {
        get("items/$showId/missing")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) ShowMissing.None else throw e
    } catch (_: FlexarrException.UnexpectedResponse) {
        ShowMissing.None
    }

/** Asks Seerr for just these seasons of a show, by its TMDB id. */
suspend fun FlexarrClient.requestSeasons(tmdbId: Int, seasons: List<Int>) {
    send("POST", "requests", body = FlexarrClient.json.encodeToString(SeasonsRequest.serializer(), SeasonsRequest("tv", tmdbId, seasons)))
}

/** Asks Sonarr to look for a season's missing episodes (just [episodes], or all of them). Main profile only. */
suspend fun FlexarrClient.searchMissing(showId: String, seasonNumber: Int, episodes: List<Int>? = null) {
    send("POST", "items/$showId/missing/search", body = FlexarrClient.json.encodeToString(MissingSearchRequest.serializer(), MissingSearchRequest(seasonNumber, episodes)))
}

/** The books Audible lists in a series that the library lacks; none from servers before 0.59 (a 404, or their web page). */
suspend fun FlexarrClient.bookSeriesMissing(seriesId: String): BookSeriesMissing =
    try {
        get("book-series/$seriesId/missing")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) BookSeriesMissing() else throw e
    } catch (_: FlexarrException.UnexpectedResponse) {
        BookSeriesMissing()
    }
