package io.github.flexarr.core

import kotlinx.serialization.Serializable

// Music requests: artists and albums from outside the library (from Deezer, matched to MusicBrainz through Lidarr),
// requesting an album and following an artist, as FlexarrKit's MusicRequests.swift. Everything here is absent from
// servers before music requests, which answer the status with their web page (or a 404): off.

/** Whether music requests are on for this profile: a Lidarr is connected, and answered just now. */
@Serializable
data class MusicRequestStatus(val configured: Boolean, val reachable: Boolean)

/** An artist or album from outside the library, with whether the household has it. */
@Serializable
data class MusicExternal(
    /** "artist" or "album". */
    val kind: String,
    /** Deezer's id; 0 for an album only Lidarr knows (requested by [musicBrainzId]). */
    val deezerId: Long = 0,
    val title: String,
    /** An album's artist. */
    val artist: String? = null,
    val artistDeezerId: Long? = null,
    val year: Int? = null,
    val image: String? = null,
    val availability: Availability = Availability.Unknown,
    /** Set when the household has it: it opens as itself. */
    val libraryItemId: String? = null,
    /** An album's kind on Deezer: "album", "ep", "single" or "compile". */
    val recordType: String? = null,
    /** An album's MusicBrainz release group, when Lidarr listed it. */
    val musicBrainzId: String? = null,
) {
    val isArtist: Boolean get() = kind == "artist"

    /** Unique within a row: an album only Lidarr knows has no Deezer id, so its release group stands in. */
    val id: String get() = "$kind-" + if (deezerId != 0L) "$deezerId" else musicBrainzId ?: title

    /** Nobody has asked for it and the library doesn't have it. */
    val canRequest: Boolean get() = availability == Availability.None && libraryItemId == null && (deezerId != 0L || musicBrainzId != null)

    /** "Album, 1997" or "EP, 2004", under an album's title. */
    val kindLine: String?
        get() {
            if (isArtist) return null
            val type = when (recordType) {
                "ep" -> "EP"
                "single" -> "Single"
                "compile" -> "Compilation"
                else -> "Album"
            }
            return listOfNotNull(type, year?.toString()).joinToString(", ")
        }
}

/**
 * An artist's releases shelved as the web shelves them: "Albums" (an album, or a kind Deezer didn't say or a newer one),
 * "EPs and singles", then "Compilations"; only the shelves with something on them.
 */
fun List<MusicExternal>.releaseShelves(): List<Pair<String, List<MusicExternal>>> =
    listOf(
        "Albums" to filter { it.recordType !in setOf("ep", "single", "compile") },
        "EPs and singles" to filter { it.recordType == "ep" || it.recordType == "single" },
        "Compilations" to filter { it.recordType == "compile" },
    ).filter { it.second.isNotEmpty() }

/** One of Discover's music rows: "artists" are round photos, "albums" square covers. */
@Serializable
data class MusicRow(
    val id: String,
    val title: String,
    val shape: String = "albums",
    val items: List<MusicExternal> = emptyList(),
) {
    val isArtists: Boolean get() = shape == "artists"
}

@Serializable
data class MusicGenre(val id: Long, val name: String, val image: String? = null)

@Serializable
data class MusicDiscover(val rows: List<MusicRow> = emptyList(), val genres: List<MusicGenre> = emptyList())

@Serializable
data class MusicSearch(val artists: List<MusicExternal> = emptyList(), val albums: List<MusicExternal> = emptyList()) {
    val isEmpty: Boolean get() = artists.isEmpty() && albums.isEmpty()
}

@Serializable
data class MusicGenreDetail(val id: Long, val name: String, val artists: List<MusicExternal> = emptyList())

@Serializable
data class MusicTrack(
    val title: String,
    val durationSeconds: Int = 0,
    val number: Int = 0,
    val disc: Int = 1,
    val explicit: Boolean = false,
)

/** An artist the household may not have: their releases, artists like them, and whether Lidarr follows them. */
@Serializable
data class MusicArtistDetail(
    val deezerId: Long,
    val name: String,
    val image: String? = null,
    val fans: Int = 0,
    val albums: List<MusicExternal> = emptyList(),
    val similar: List<MusicExternal> = emptyList(),
    /** Lidarr watches for their new releases. */
    val followed: Boolean = false,
    val availability: Availability = Availability.Unknown,
    val libraryItemId: String? = null,
)

@Serializable
data class MusicAlbumDetail(
    val deezerId: Long,
    val title: String,
    val artist: String,
    val artistDeezerId: Long? = null,
    val image: String? = null,
    val year: Int? = null,
    val recordType: String? = null,
    val genres: List<String> = emptyList(),
    val tracks: List<MusicTrack> = emptyList(),
    val durationSeconds: Int = 0,
    val availability: Availability = Availability.Unknown,
    val libraryItemId: String? = null,
) {
    /** The album as a row knows it, for its request. */
    val summary: MusicExternal
        get() = MusicExternal("album", deezerId, title, artist, artistDeezerId, year, image, availability, libraryItemId, recordType)
}

/** A library artist's page from outside: albums of theirs the library lacks, artists like them, and whether they're followed. */
@Serializable
data class MusicArtistExtras(
    val missing: List<MusicExternal> = emptyList(),
    val similar: List<MusicExternal> = emptyList(),
    val followed: Boolean = false,
    /** The artist on Deezer, for their outside page; null when Deezer doesn't know them. */
    val deezerId: Long? = null,
)

@Serializable
internal data class MusicRequestBody(
    val kind: String,
    val deezerId: Long? = null,
    val musicBrainzId: String? = null,
    val artistId: String? = null,
)

/**
 * Whether this profile can request music. Servers before music requests answer with their web page (200 text/html)
 * or a 404, which read as off; so does any other answer that doesn't read as a status. Sign-in and network failures
 * still throw.
 */
suspend fun FlexarrClient.musicRequestStatus(): MusicRequestStatus =
    try {
        get("requests/music/status")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) MusicRequestOff else throw e
    } catch (_: FlexarrException.UnexpectedResponse) {
        // An HTML page, or JSON that isn't a status.
        MusicRequestOff
    }

private val MusicRequestOff = MusicRequestStatus(configured = false, reachable = false)

/** Discover's music rows (like artists played here, new from them, charts) and genre tiles, from Deezer. */
suspend fun FlexarrClient.musicDiscover(): MusicDiscover = get("requests/music/discover")

/** A Deezer genre's artists. */
suspend fun FlexarrClient.musicGenre(id: Long): MusicGenreDetail = get("requests/music/genres/$id")

/** Any artist or album Deezer knows, marked if the household has it. */
suspend fun FlexarrClient.searchMusic(query: String): MusicSearch = get("requests/music/search", mapOf("q" to query))

suspend fun FlexarrClient.musicArtist(deezerId: Long): MusicArtistDetail = get("requests/music/artist/$deezerId")

suspend fun FlexarrClient.musicAlbum(deezerId: Long): MusicAlbumDetail = get("requests/music/album/$deezerId")

/** A library artist's albums the library lacks, artists like them, and whether Lidarr follows them. */
suspend fun FlexarrClient.musicArtistExtras(libraryItemId: String): MusicArtistExtras = get("requests/music/library-artist/$libraryItemId")

/** Asks Lidarr for just this album (by Deezer id, or its release group for one only Lidarr lists). */
suspend fun FlexarrClient.requestAlbum(album: MusicExternal) {
    val body = if (album.deezerId != 0L) MusicRequestBody("album", deezerId = album.deezerId) else MusicRequestBody("album", musicBrainzId = album.musicBrainzId)
    send("POST", "requests/music", body = FlexarrClient.json.encodeToString(MusicRequestBody.serializer(), body))
}

/** Follows an artist from outside the library: Lidarr gets their new releases from now on. */
suspend fun FlexarrClient.followArtist(deezerId: Long) {
    send("POST", "requests/music", body = FlexarrClient.json.encodeToString(MusicRequestBody.serializer(), MusicRequestBody("artist", deezerId = deezerId)))
}

/** Follows a library artist, by its id here. */
suspend fun FlexarrClient.followLibraryArtist(artistId: String) {
    send("POST", "requests/music", body = FlexarrClient.json.encodeToString(MusicRequestBody.serializer(), MusicRequestBody("artist", artistId = artistId)))
}
