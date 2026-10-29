package io.github.flexarr.core

import kotlinx.serialization.Serializable

// Albums and playlists downloaded whole, as the iPhone app's LocalCollection: each track is a download of its own (the
// server sends it as it is, or as AAC when it must), and the collection holds them in order under one picture and name.

@Serializable
enum class CollectionKind { Album, Playlist }

/**
 * An album or playlist kept on this phone: its tracks (as downloads of their own, in order) under one picture and name,
 * browsable and playable with no connection. A track in two collections is downloaded once. Saved as JSON.
 */
@Serializable
data class LocalCollection(
    /** The album's item id, or the playlist's id. */
    val id: String,
    /** Whose it is: each profile sees only its own. */
    val profileId: String,
    val kind: CollectionKind,
    val title: String,
    /** The artist, for an album. */
    val subtitle: String? = null,
    /** The album itself, for Now Playing's album name and the page's colour. */
    val album: ItemSummary? = null,
    val accentColor: String? = null,
    val trackIds: List<String> = emptyList(),
    /** Its saved picture: an album's cover (shared with its tracks), or a playlist's chosen cover. */
    val imageFileName: String? = null,
    /** About how big it'll be, from the server, before the files are here. */
    val estimatedBytes: Long? = null,
    val createdAtMillis: Long = 0,
)

/** Where a collection's tracks stand, taken together. */
sealed interface CollectionStatus {
    /** Tracks are still being asked for, or the server is getting them ready. */
    data object Preparing : CollectionStatus
    /** Some are here, or coming ([fraction] of the whole, 0 to 1). */
    data class Downloading(val fraction: Double) : CollectionStatus
    data object Downloaded : CollectionStatus
    data class Failed(val message: String) : CollectionStatus
}

object CollectionRules {
    /**
     * What a collection's [tracks] (its downloads in order, null for one not asked for) add up to: one that failed fails
     * the whole; all here is downloaded; any here or coming is downloading, by how much of the whole has come; otherwise
     * it's still being got ready. A track never asked for (the requests were cut short) fails it, unless they're still
     * being made ([queueing]). [progress] is how far a transfer under way has got.
     */
    fun status(tracks: List<LocalDownload?>, queueing: Boolean, progress: (LocalDownload) -> Double = { it.progress }): CollectionStatus {
        if (!queueing && tracks.any { it == null }) return CollectionStatus.Failed("Some tracks weren’t downloaded. Try again.")
        if (tracks.isEmpty()) return CollectionStatus.Preparing
        tracks.firstOrNull { it?.phase == LocalPhase.Failed }?.let { return CollectionStatus.Failed(it.failure ?: "Couldn’t download this.") }
        val fractions = tracks.map { track ->
            when {
                track == null -> 0.0
                track.isDownloaded -> 1.0
                track.phase == LocalPhase.Downloading -> progress(track).coerceIn(0.0, 1.0)
                else -> 0.0
            }
        }
        if (fractions.all { it >= 1.0 }) return CollectionStatus.Downloaded
        if (tracks.any { it != null && (it.isDownloaded || it.phase == LocalPhase.Downloading) }) return CollectionStatus.Downloading(fractions.average())
        return CollectionStatus.Preparing
    }

    /** The tracks of [removing] that can go with it: those no other of the profile's collections still holds. */
    fun tracksToDelete(removing: LocalCollection, collections: List<LocalCollection>): List<String> {
        val kept = collections.filter { it.profileId == removing.profileId && it.id != removing.id }.flatMap { it.trackIds }.toSet()
        return removing.trackIds.filter { it !in kept }.distinct()
    }

    /** The cover an album's tracks and the album itself share, saved once. */
    fun coverName(albumId: String): String = "cover-${albumId.replace(Regex("[^A-Za-z0-9_-]"), "_")}.jpg"

    /** A playlist's chosen cover. */
    fun playlistCoverName(playlistId: String): String = "playlist-${playlistId.replace(Regex("[^A-Za-z0-9_-]"), "_")}.jpg"

    /** "1985   9 tracks   41m   Downloaded", or how many are here so far, for a downloaded album's or playlist's page. */
    fun facts(collection: LocalCollection, here: List<ItemSummary>): List<String> = buildList {
        collection.album?.releaseYear?.let { add(it.toString()) }
        val total = collection.trackIds.size
        add(if (total == 1) "1 track" else "$total tracks")
        totalRuntime(here)?.let(::add)
        add(if (here.size == total) "Downloaded" else "${here.size} here")
    }
}
