package io.github.flexarr.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.serializer
import java.time.LocalDate

// The watchlist, as FlexarrKit's Watchlist.swift: films and shows a profile wants to watch later, in the library or
// not. Absent from servers before 0.56: they answer a GET for a path they don't know with the web app's index.html
// (200, text/html), which the client turns down as UnexpectedResponse, so that (or a 404) reads as no watchlist.

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
    /** Titles not in the library: the subscription services it's on in the household's region, a few at most. */
    val streaming: List<String>? = null,
) {
    val isMovie: Boolean get() = mediaType == "movie"

    /** "On Netflix", or "Netflix, Prime Video"; null when it isn't known to stream anywhere. */
    val streamingLine: String?
        get() {
            val services = streaming?.map(String::trim)?.filter(String::isNotEmpty).orEmpty()
            return when (services.size) {
                0 -> null
                1 -> "On ${services[0]}"
                else -> services.joinToString(", ")
            }
        }

    /** "Film, 2014" or "Show, 2011": for a title the library hasn't got. */
    val kindLine: String get() = kindLine(mediaType, year)

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

/** Films, shows, or both: the Watchlist screen's kind filter, as the web's. */
enum class WatchlistKind(val label: String) {
    All("Films and shows"),
    Films("Films"),
    Shows("Shows");

    fun matches(entry: WatchlistEntry): Boolean = when (this) {
        All -> true
        Films -> entry.mediaType == "movie"
        Shows -> entry.mediaType == "tv"
    }
}

/** How the Watchlist screen orders its titles, as the web's. */
enum class WatchlistSort(val label: String) {
    /** Ready, then on its way, coming, not available; newest added first within each. */
    ReadyFirst("Ready first"),
    RecentlyAdded("Recently added"),
    /** Soonest (oldest) first; titles with no date last. */
    ReleaseDate("Release date"),
    Title("Title");

    fun sorted(entries: List<WatchlistEntry>): List<WatchlistEntry> = when (this) {
        ReadyFirst -> entries.sortedWith(compareBy<WatchlistEntry> { rank(it.status) }.then(newestAdded))
        RecentlyAdded -> entries.sortedWith(newestAdded)
        ReleaseDate -> entries.sortedBy { releaseKey(it) }
        Title -> entries.sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { titleKey(it) })
    }

    private companion object {
        val newestAdded: Comparator<WatchlistEntry> = compareByDescending(nullsFirst<String>()) { it.addedAt }

        fun rank(status: WatchlistStatus) = when (status) {
            WatchlistStatus.Ready -> 0
            WatchlistStatus.OnItsWay -> 1
            WatchlistStatus.Coming -> 2
            WatchlistStatus.NotAvailable -> 3
            WatchlistStatus.Unknown -> 4
        }

        fun releaseKey(entry: WatchlistEntry): String =
            entry.item?.releaseDate ?: entry.releaseDate ?: entry.year?.let { "$it-01-01" } ?: "9999"

        /** Letters, digits and spaces only, as the web sorts ignoring punctuation. */
        fun titleKey(entry: WatchlistEntry): String = entry.title.filter { it.isLetterOrDigit() || it.isWhitespace() }.trim()
    }
}

/**
 * What's on the profile's watchlist, without where each has got to: library ids (linked, or matched by TMDB id) and
 * "movie:123" / "tv:456" keys. Cheap for the server (no Seerr), for poster menus that offer Add or Remove.
 */
@Serializable
data class WatchlistKeys(val libraryItemIds: List<String> = emptyList(), val titles: List<String> = emptyList())

@Serializable
internal data class WatchlistAddBody(val libraryItemId: String? = null, val mediaType: String? = null, val tmdbId: Int? = null)

@Serializable
internal data class TitleRequestBody(val mediaType: String, val tmdbId: Int)

/** "Film, 2014" or "Show, 2011" (just "Film" or "Show" without a year), for "movie" or "tv". */
fun kindLine(mediaType: String, year: Int?): String =
    listOfNotNull(if (mediaType == "tv") "Show" else "Film", year?.toString()).joinToString(", ")

/** Whether Seerr is set up on the server (and answering), so search can look beyond the library. */
@Serializable
data class RequestsStatus(val configured: Boolean = false, val reachable: Boolean = false)

// Client

/**
 * The profile's watchlist, newest first; null from servers before 0.56, which have none: they answer with the web app's
 * page (200, text/html) for a path they don't know. A 404 (a proxy in front, say) reads the same.
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

/** What's on the watchlist ([WatchlistKeys]); null from servers before 0.56, as [watchlist]. */
suspend fun FlexarrClient.watchlistKeys(): WatchlistKeys? {
    val text = try {
        sendRaw("GET", "watchlist/keys")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) return null else throw e
    } catch (_: FlexarrException.UnexpectedResponse) {
        return null
    }
    return decodeText(serializer(), text)
}

/**
 * Up to [take] library titles from the watchlist that are ready to play, newest added first, for the widget: the
 * server's `watchlist/ready`, which doesn't ask Seerr. A server without it (before it, or without watchlists at all)
 * is asked for the whole watchlist instead, and its ready titles kept; one without a watchlist has none.
 */
suspend fun FlexarrClient.readyFromWatchlist(take: Int): List<ItemSummary> {
    val text = try {
        send("GET", "watchlist/ready", mapOf("take" to "$take"))
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) null else throw e
    } catch (_: FlexarrException.UnexpectedResponse) {
        null
    }
    if (text != null) return decodeText(ListSerializer(ItemSummary.serializer()), text).take(take)
    return watchlist().orEmpty().asSequence().filter { it.status == WatchlistStatus.Ready }.mapNotNull { it.item }.take(take).toList()
}

/** Whether search can look beyond the library ([RequestsStatus]); null from a server that doesn't say. */
suspend fun FlexarrClient.requestsStatus(): RequestsStatus? = try {
    decodeText(RequestsStatus.serializer(), sendRaw("GET", "requests/status"))
} catch (e: FlexarrException.Http) {
    if (e.status == 404) null else throw e
} catch (_: FlexarrException.UnexpectedResponse) {
    null
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

/** Takes a title from outside the library off, by "movie" or "tv" and its TMDB id. Already off is fine. */
suspend fun FlexarrClient.removeFromWatchlist(mediaType: String, tmdbId: Int) {
    ignoringGone { send("DELETE", "watchlist", mapOf("mediaType" to mediaType, "tmdbId" to "$tmdbId")) }
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
