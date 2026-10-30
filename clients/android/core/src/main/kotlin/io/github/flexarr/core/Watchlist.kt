package io.github.flexarr.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.serializer
import java.time.LocalDate

// The watchlist, as FlexarrKit's Watchlist.swift: films and shows a profile wants to watch later, in the library or
// not. Absent from servers before 0.56, which answer 404 for it.

/** Where a title on the watchlist has got to. */
@Serializable(with = WatchlistStatus.Serializer::class)
enum class WatchlistStatus(override val raw: String) : ForwardCompatibleEnum {
    /** In the library: it plays. */
    Ready("Ready"),

    /** Requested, or Sonarr or Radarr are getting it. */
    OnItsWay("OnItsWay"),

    /** Not out yet ([WatchlistEntry.releaseDate] says when). */
    Coming("Coming"),

    /** Out, but nobody's asked for it: it can be requested. */
    NotAvailable("NotAvailable"),

    Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<WatchlistStatus>("WatchlistStatus", entries, Unknown)
}

@Serializable
data class WatchlistEntry(
    val id: String,
    /** "movie" or "tv". */
    val mediaType: String,
    val tmdbId: Int? = null,
    val title: String,
    val year: Int? = null,
    /** Server-relative artwork, as [Artwork]'s. */
    val poster: String? = null,
    val backdrop: String? = null,
    val addedAt: String? = null,
    val status: WatchlistStatus = WatchlistStatus.Unknown,
    /** "yyyy-MM-dd", when known. */
    val releaseDate: String? = null,
    val libraryItemId: String? = null,
    /** The title in the library when it's [WatchlistStatus.Ready], for the usual poster card. */
    val item: ItemSummary? = null,
    /** "flexarr", or "trakt" for a title brought across from Trakt. */
    val source: String = "flexarr",
) {
    val isMovie: Boolean get() = mediaType == "movie"

    /** [releaseDate] as a date, or null when there's none (or it isn't one). */
    val releaseDay: LocalDate? get() = releaseDate?.let { runCatching { LocalDate.parse(it.take(10)) }.getOrNull() }
}

/** What adding a title says: the entry, and whether adding it also requested it (the profile asks for that). */
@Serializable
data class WatchlistAdded(val entry: WatchlistEntry, val requested: Boolean = false)

/** The filters along the top of the Watchlist screen. */
enum class WatchlistFilter(val label: String) {
    Everything("Everything"),
    Ready("Ready to watch"),
    OnItsWay("On its way"),
    Coming("Coming"),
    NotAvailable("Not available");

    fun matches(entry: WatchlistEntry): Boolean = when (this) {
        Everything -> true
        Ready -> entry.status == WatchlistStatus.Ready
        OnItsWay -> entry.status == WatchlistStatus.OnItsWay
        Coming -> entry.status == WatchlistStatus.Coming
        NotAvailable -> entry.status == WatchlistStatus.NotAvailable
    }
}

@Serializable
internal data class WatchlistAddBody(val libraryItemId: String? = null, val mediaType: String? = null, val tmdbId: Int? = null)

@Serializable
internal data class TitleRequestBody(val mediaType: String, val tmdbId: Int)

// Client

/**
 * The profile's watchlist, newest first; null from servers before 0.56, which have none (they answer 404, or the web
 * app's page for a path they don't know).
 */
suspend fun FlexarrClient.watchlist(): List<WatchlistEntry>? {
    val text = try {
        sendRaw("GET", "watchlist")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) return null else throw e
    } catch (_: FlexarrException.UnexpectedResponse) {
        return null
    }
    return decodeText(serializer(), text)
}

/** Puts a library film or show on the watchlist (a season or episode stands for its show). Already there is fine. */
suspend fun FlexarrClient.addToWatchlist(libraryItemId: String): WatchlistAdded =
    sendDecoding("POST", "watchlist", WatchlistAddBody(libraryItemId = libraryItemId))

/** Puts a title from outside the library on the watchlist, by "movie" or "tv" and its TMDB id. */
suspend fun FlexarrClient.addToWatchlist(mediaType: String, tmdbId: Int): WatchlistAdded =
    sendDecoding("POST", "watchlist", WatchlistAddBody(mediaType = mediaType, tmdbId = tmdbId))

/** Takes an entry off. One already gone (404) is fine: it's off. */
suspend fun FlexarrClient.removeFromWatchlist(entryId: String) {
    ignoringGone { send("DELETE", "watchlist/$entryId") }
}

/** Takes a library title off from its own page, where the entry's id isn't known. */
suspend fun FlexarrClient.removeLibraryItemFromWatchlist(libraryItemId: String) {
    ignoringGone { send("DELETE", "watchlist", mapOf("libraryItemId" to libraryItemId)) }
}

/** Asks Seerr for a title, as the web's Request button does. */
suspend fun FlexarrClient.requestTitle(mediaType: String, tmdbId: Int) {
    send("POST", "requests", body = FlexarrClient.json.encodeToString(TitleRequestBody.serializer(), TitleRequestBody(mediaType, tmdbId)))
}

private suspend fun ignoringGone(action: suspend () -> Unit) {
    try {
        action()
    } catch (e: FlexarrException.Http) {
        if (e.status != 404) throw e
    }
}
