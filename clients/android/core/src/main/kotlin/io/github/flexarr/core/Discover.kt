package io.github.flexarr.core

import kotlinx.serialization.Serializable

// Discover: films and shows from outside the library (from Seerr, which gets them from TMDB), requesting them, and the
// library by genre, as FlexarrKit's Discover.swift.

/** Whether requests are set up: a Seerr server is configured, and answered just now. */
@Serializable
data class RequestStatus(val configured: Boolean, val reachable: Boolean)

/** Where a title from outside the library stands. */
@Serializable(with = Availability.Serializer::class)
enum class Availability(override val raw: String) : ForwardCompatibleEnum {
    /** Not in the library and not requested. */
    None("None"),
    Requested("Requested"),
    PartlyAvailable("PartlyAvailable"),
    Available("Available"),
    /** A state from a newer server: shown as not requestable. */
    Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<Availability>("Availability", entries, Unknown)
}

/** A film or show TMDB knows (via Seerr), with whether the library has it. */
@Serializable
data class ExternalTitle(
    val tmdbId: Int,
    /** "movie" or "tv". */
    val mediaType: String,
    val title: String,
    val year: Int? = null,
    val overview: String? = null,
    val poster: String? = null,
    val backdrop: String? = null,
    val rating: Double = 0.0,
    val availability: Availability = Availability.Unknown,
    /** Set when it's in the library: it opens as itself. */
    val libraryItemId: String? = null,
) {
    val id: String get() = "$mediaType-$tmdbId"
    val isShow: Boolean get() = mediaType == "tv"
}

/** One of Discover's rows: trending, popular films, popular shows, coming to cinemas. */
@Serializable
data class ExternalRow(
    val id: String,
    val title: String,
    val items: List<ExternalTitle> = emptyList(),
)

@Serializable
data class ExternalPerson(
    val name: String,
    val character: String? = null,
    val image: String? = null,
)

/** Everything worth knowing about a film or show the library doesn't have, from Seerr (which gets it from TMDB). */
@Serializable
data class ExternalDetail(
    val tmdbId: Int,
    val mediaType: String,
    val title: String,
    val tagline: String? = null,
    val year: Int? = null,
    val overview: String? = null,
    val poster: String? = null,
    val backdrop: String? = null,
    val genres: List<String> = emptyList(),
    val runtimeMinutes: Int? = null,
    val seasons: Int? = null,
    val certification: String? = null,
    val status: String? = null,
    val tmdbRating: Double? = null,
    /** Rotten Tomatoes critics, percent. */
    val criticScore: Int? = null,
    /** Rotten Tomatoes audience, percent. */
    val audienceScore: Int? = null,
    val imdbRating: Double? = null,
    /** The title's trailer on YouTube, for a Trailer button (none yet on any app). */
    val trailerYouTubeId: String? = null,
    val cast: List<ExternalPerson> = emptyList(),
    /** Directors of a film, creators of a show. */
    val madeBy: List<String> = emptyList(),
    /** A film's studio, a show's network. */
    val studio: String? = null,
    val availability: Availability = Availability.Unknown,
    val libraryItemId: String? = null,
    val recommendations: List<ExternalTitle> = emptyList(),
) {
    val isShow: Boolean get() = mediaType == "tv"

    /** The title as Discover's rows know it, for showing the page's header before the rest arrives. */
    val summary: ExternalTitle
        get() = ExternalTitle(tmdbId, mediaType, title, year, overview, poster, backdrop, tmdbRating ?: 0.0, availability, libraryItemId)
}

/** A genre in the library, with how many films and shows are in it and a backdrop to show it by. */
@Serializable
data class Genre(
    val name: String,
    val movies: Int = 0,
    val shows: Int = 0,
    val backdrop: String? = null,
)

@Serializable
internal data class TitleRequest(val mediaType: String, val tmdbId: Int)

/** Whether this server can take requests. Servers before requests answer 404: no. */
suspend fun FlexarrClient.requestStatus(): RequestStatus =
    try {
        get("requests/status")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) RequestStatus(configured = false, reachable = false) else throw e
    }

/** Discover's rows from Seerr; none without a Seerr server. */
suspend fun FlexarrClient.discoverRows(): List<ExternalRow> = get("requests/discover")

/** Any film or show TMDB knows, not just the library's, marked if the library has it. */
suspend fun FlexarrClient.searchEverything(query: String): List<ExternalTitle> = get("requests/search", mapOf("q" to query))

suspend fun FlexarrClient.externalTitle(title: ExternalTitle): ExternalDetail = get("requests/title/${title.mediaType}/${title.tmdbId}")

/** Asks Seerr for the title, and the server remembers who asked so they hear when it's in. */
suspend fun FlexarrClient.request(title: ExternalTitle) {
    send("POST", "requests", body = FlexarrClient.json.encodeToString(TitleRequest(title.mediaType, title.tmdbId)))
}

/** The library's genres, biggest first. */
suspend fun FlexarrClient.genres(): List<Genre> = get("genres")

/** What the library has in a genre, best rated first; [kind] narrows it to films or shows. */
suspend fun FlexarrClient.genreItems(name: String, kind: MediaKind? = null, skip: Int = 0, take: Int = 120): Paged<ItemSummary> =
    // One segment, whatever's in the name; the rest of it is percent-encoded as the request is made ("Science
    // Fiction", "Action & Adventure").
    get(
        "genres/${FlexarrClient.pathSegment(name)}/items",
        buildMap {
            if (skip > 0) put("skip", skip.toString())
            put("take", take.toString())
            if (kind != null) put("kind", kind.raw)
        },
    )
