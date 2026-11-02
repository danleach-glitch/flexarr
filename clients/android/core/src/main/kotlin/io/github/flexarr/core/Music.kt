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

/**
 * A music library's albums (its items are its artists), A–Z, newest added or by year, optionally in one genre (on the
 * album or its artist). Null from servers before the endpoint (0.59), which answer 404: they have no Albums or Genres.
 */
suspend fun FlexarrClient.libraryAlbums(
    libraryId: String,
    sort: LibrarySort = LibrarySort.Title,
    genre: String? = null,
    skip: Int = 0,
    take: Int = 60,
): Paged<ItemSummary>? =
    try {
        get(
            "libraries/$libraryId/albums",
            buildMap {
                if (sort != LibrarySort.Title) put("sort", sort.raw)
                if (!genre.isNullOrEmpty()) put("genre", genre)
                if (skip > 0) put("skip", skip.toString())
                put("take", take.toString())
            },
        )
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) null else throw e
    }

/** A music library's genres counted by album, each with an artist's fanart. Null from servers before 0.59. */
suspend fun FlexarrClient.libraryAlbumGenres(libraryId: String): List<GenreCount>? =
    try {
        get("libraries/$libraryId/album-genres")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) null else throw e
    }

/** Up to 200 of a genre's tracks in a music library: by artist, album and track, or shuffled by the server. */
suspend fun FlexarrClient.genreTracks(libraryId: String, genre: String, shuffled: Boolean = false): List<ItemSummary> =
    get("libraries/$libraryId/tracks", buildMap {
        put("genre", genre)
        if (shuffled) put("shuffle", "true")
    })

/** Artist radio: about fifty tracks seeded from an artist (or an album's artist), theirs and others' in their genres. */
suspend fun FlexarrClient.radio(id: String): List<ItemSummary> = get("items/$id/radio")

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
 * A music library's Browse: its hero (servers from 0.59; empty before) and its rows, as the server lists them for the
 * profile. When it can't load (a timeout, a server without it), no hero and the profile's playlists as one row instead,
 * as the page had them before the server's rows.
 */
suspend fun FlexarrClient.musicBrowse(libraryId: String): Home {
    try {
        return libraryBrowse(libraryId)
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
    val rows = if (lists.isEmpty()) emptyList() else listOf(HomeRow(MUSIC_PLAYLISTS_ROW_ID, "Your playlists", HomeRowStyle.Playlists, emptyList(), playlists = lists))
    return Home(featured = emptyList(), rows = rows)
}

/** A music library's rows alone: see [musicBrowse]. */
suspend fun FlexarrClient.musicRows(libraryId: String): List<HomeRow> = musicBrowse(libraryId).rows

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

/** Where an album from a music library's hero starts: the track at [index], [position] seconds in (null: its start). */
data class AlbumStart(val index: Int, val position: Double?)

/**
 * Where an album's Play or Resume starts in its [tracks]: at [resume] (the track the server says it carries on from),
 * part way in when that was left half way, otherwise at the first track. A resume track no longer on the album starts
 * it from the top.
 */
fun albumStart(tracks: List<ItemSummary>, resume: ItemSummary?): AlbumStart {
    val index = resume?.let { r -> tracks.indexOfFirst { it.id == r.id } }?.takeIf { it >= 0 } ?: return AlbumStart(0, null)
    val position = resume.userData.takeIf { !it.watched && it.positionSeconds > 0 }?.positionSeconds
    return AlbumStart(index, position)
}

/** A music library's views, switched at the top of its page as a film library's are. */
enum class MusicView(val label: String) {
    Browse("Browse"),
    Artists("Artists"),
    Albums("Albums"),
    Genres("Genres"),
    ;

    /** Albums and Genres need a server from 0.59 ([libraryAlbums] isn't null); before it, Browse and Artists. */
    val needsAlbums: Boolean get() = this == Albums || this == Genres

    /** How its grid can be sorted. */
    val sorts: List<LibrarySort>
        get() = when (this) {
            Artists -> listOf(LibrarySort.Title, LibrarySort.Added)
            Albums -> listOf(LibrarySort.Title, LibrarySort.Added, LibrarySort.Year)
            else -> emptyList()
        }

    companion object {
        /** The views to offer: all four, or without Albums and Genres for a server that has no albums list. */
        fun offered(albums: Boolean): List<MusicView> = entries.filter { albums || !it.needsAlbums }
    }
}

/** "1 artist", "1,204 artists". */
fun artistCount(total: Int): String = if (total == 1) "1 artist" else "${Format.count(total)} artists"

/** "1 album", "38 albums". */
fun albumCount(total: Int): String = if (total == 1) "1 album" else "${Format.count(total)} albums"
