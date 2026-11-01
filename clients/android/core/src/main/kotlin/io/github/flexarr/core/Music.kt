package io.github.flexarr.core

import kotlinx.coroutines.CancellationException
import kotlinx.serialization.Serializable

// Music: lyrics, playlists, and the requests and words the music screens need, as FlexarrKit's Models.swift,
// FlexarrClient.swift and Presentation.swift have them for the Apple apps.

/** One line of a track's lyrics; [time] (seconds into the track) is set throughout synced lyrics and never otherwise. */
@Serializable
data class LyricLine(val time: Double? = null, val text: String)

/** A track's lyrics: synced to the music when every line has a time, otherwise plain text. */
@Serializable
data class Lyrics(val synced: Boolean, val lines: List<LyricLine>) {
    /**
     * The line being sung [time] seconds in: the last whose time has come, or null before the first (and always for
     * lyrics that aren't synced).
     */
    fun activeLine(time: Double): Int? {
        if (!synced) return null
        var active: Int? = null
        for ((index, line) in lines.withIndex()) {
            val at = line.time ?: continue
            if (at <= time) active = index else break
        }
        return active
    }
}

/** One of the profile's music playlists, as a list shows it. */
@Serializable
data class PlaylistSummary(
    val id: String,
    val name: String,
    val trackCount: Int,
    val durationSeconds: Double,
    /** Up to four of its tracks' album covers, first tracks first, for a mosaic. */
    val covers: List<String> = emptyList(),
    /** A cover chosen for it, shown instead of the mosaic. */
    val cover: String? = null,
    val createdAt: String? = null,
    val updatedAt: String? = null,
) {
    /** "12 tracks". */
    val trackCountLabel: String get() = if (trackCount == 1) "1 track" else "$trackCount tracks"
}

/** The id of the "Made for you" row of the profile's daily music mixes, on Home and a music library's page. */
const val MUSIC_MIXES_ROW_ID = "music-mixes"

/**
 * One of the profile's daily music mixes ("Made for you"): the same all day, new tomorrow, so an id from yesterday is
 * not found.
 */
@Serializable
data class MixSummary(
    val id: String,
    val name: String,
    /** Who's in it, or what it's made from: "Made from the artists you play". */
    val description: String = "",
    /** Up to four of its tracks' album covers, for a mosaic. */
    val covers: List<String> = emptyList(),
    val trackCount: Int = 0,
)

/** A mix with its tracks in order. */
@Serializable
data class MixDetail(val summary: MixSummary, val tracks: List<ItemSummary> = emptyList())

/** A place in a playlist: the entry (what's moved or removed) and the track in it. */
@Serializable
data class PlaylistTrack(val entryId: Int, val track: ItemSummary)

@Serializable
data class PlaylistDetail(val summary: PlaylistSummary, val tracks: List<PlaylistTrack> = emptyList())

// Requests

/**
 * Every track an artist (or one album) has, in the order a record collection plays: albums oldest first, then disc
 * and track. Servers before the endpoint answer 404, so the albums are fetched one by one instead.
 */
suspend fun FlexarrClient.tracks(id: String): List<ItemSummary> =
    try {
        get("items/$id/tracks")
    } catch (e: FlexarrException.Http) {
        if (e.status != 404) throw e
        val detail = item(id)
        if (detail.item.kind == MediaKind.Album) {
            detail.children.filter { it.kind == MediaKind.Track }
        } else {
            // Oldest album first, as the endpoint orders them; the server lists an artist's albums newest first.
            detail.children.filter { it.kind == MediaKind.Album }
                .sortedWith(compareBy<ItemSummary>({ it.year ?: Int.MAX_VALUE }, { it.title }))
                .flatMap { album -> item(album.id).children.filter { it.kind == MediaKind.Track } }
        }
    }

/** A track's lyrics, or null when it has none (servers before lyrics have none for any track). */
suspend fun FlexarrClient.lyrics(id: String): Lyrics? =
    try {
        get<Lyrics>("items/$id/lyrics")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) null else throw e
    }

/** The signed-in profile's music playlists. Servers before playlists have none. */
suspend fun FlexarrClient.playlists(): List<PlaylistSummary> =
    try {
        get("playlists")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) emptyList() else throw e
    }

/** A playlist with its tracks in order. */
suspend fun FlexarrClient.playlist(id: String): PlaylistDetail = get("playlists/$id")

/** One of today's music mixes with its tracks in order. */
suspend fun FlexarrClient.mix(id: String): MixDetail = get("music/mixes/$id")

/** The id of the row of the profile's playlists a music library shows when its own rows can't load. */
const val MUSIC_PLAYLISTS_ROW_ID = "playlists"

/**
 * A music library's rows, as the server lists them for the profile. When they can't load (a timeout, a server
 * without them), the profile's playlists as one row instead, as the page had them before the server's rows.
 */
suspend fun FlexarrClient.musicRows(libraryId: String): List<HomeRow> {
    try {
        return libraryBrowse(libraryId).rows
    } catch (e: CancellationException) {
        throw e
    } catch (_: Exception) {
        // The playlists instead, below.
    }
    val lists = try {
        playlists()
    } catch (e: CancellationException) {
        throw e
    } catch (_: Exception) {
        // Neither: the artists still show.
        emptyList()
    }
    if (lists.isEmpty()) return emptyList()
    return listOf(HomeRow(MUSIC_PLAYLISTS_ROW_ID, "Your playlists", HomeRowStyle.Playlists, emptyList(), playlists = lists))
}

// Words

/**
 * Who to name as the artist of a track or album: the track's own artist when it has one (a compilation's tracks, a
 * featured artist), otherwise the album's, as the server sends it alongside the title. A podcast episode's is its show.
 */
val ItemSummary.artistName: String?
    get() = when {
        kind.isMusic -> trackArtist ?: seriesTitle
        kind == MediaKind.PodcastEpisode -> seriesTitle
        else -> null
    }

/** "46m" for an album's tracks played end to end, or null when none has a length. */
fun totalRuntime(tracks: List<ItemSummary>): String? {
    val total = tracks.sumOf { it.durationSeconds ?: 0.0 }
    return if (total > 0) Format.runtime(total) else null
}

/** Tracks in the order they play, grouped by disc. One group when the album is a single disc (or no disc is numbered). */
fun discs(tracks: List<ItemSummary>): List<Pair<Int, List<ItemSummary>>> =
    tracks.groupBy { it.discNumber ?: 1 }.toSortedMap().map { (disc, tracks) -> disc to tracks }
