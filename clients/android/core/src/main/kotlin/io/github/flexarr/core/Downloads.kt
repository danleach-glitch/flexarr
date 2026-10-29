package io.github.flexarr.core

import kotlinx.serialization.Serializable
import okhttp3.Request
import java.time.Instant

// Downloads for watching and listening with no connection, as FlexarrKit's OfflineDownload and the iPhone app's
// DownloadStore: the server gets a file ready (converting it to the size asked for), the phone fetches it, and what's
// watched offline is sent back later. The rules for what a download does next live here, so they're tested off the device.

@Serializable(with = DownloadQuality.Serializer::class)
enum class DownloadQuality(override val raw: String, val label: String) : ForwardCompatibleEnum {
    Original("Original", "Original"),
    High("High", "High (1080p)"),
    Medium("Medium", "Medium (720p)"),
    Small("Small", "Small (480p)"),
    /** A size from a newer server: listed, but never asked for. */
    Unknown("", "Other");

    object Serializer : ForwardCompatibleEnumSerializer<DownloadQuality>("DownloadQuality", entries, Unknown)
}

@Serializable(with = DownloadStatus.Serializer::class)
enum class DownloadStatus(override val raw: String) : ForwardCompatibleEnum {
    Queued("Queued"), Preparing("Preparing"), Ready("Ready"), Received("Received"), Failed("Failed"), Expired("Expired"),
    /** Deleted from a phone: a podcast episode the server won't offer for auto-download again. */
    Removed("Removed"),
    Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<DownloadStatus>("DownloadStatus", entries, Unknown)
}

/** A size a title can be downloaded at, about how big it'll be, and whether it can be (an Original a phone can't play can't). */
@Serializable
data class DownloadOption(val quality: DownloadQuality, val estimatedBytes: Long, val available: Boolean)

/** A download on the server: being got ready, ready to fetch, or fetched; with what's needed to play it offline. */
@Serializable
data class OfflineDownload(
    val id: String,
    val itemId: String,
    val quality: DownloadQuality,
    val status: DownloadStatus,
    val progress: Double = 0.0,
    val sizeBytes: Long? = null,
    val error: String? = null,
    val item: ItemSummary,
    val markers: List<Marker> = emptyList(),
    val durationSeconds: Double = 0.0,
    val nextId: String? = null,
    /** What the file comes as once it's ready: "….mp4", or a track's or podcast episode's "….mp3". Absent from servers before music downloads. */
    val fileName: String? = null,
)

/** An episode the phone should have without being asked, from a show it auto-downloads. */
@Serializable
data class PodcastAutoDownload(val episodeId: String, val podcastId: String)

@Serializable
internal data class DownloadRequestBody(val itemId: String, val quality: DownloadQuality)

@Serializable
internal data class OfflineProgressBody(val positionSeconds: Double, val finished: Boolean, val playedAt: String)

/** The sizes [itemId] can be downloaded at. */
suspend fun FlexarrClient.downloadOptions(itemId: String): List<DownloadOption> = get("items/$itemId/download-options")

/** Asks the server to get [itemId] ready at [quality] (the same one again, if it's already on its way). */
suspend fun FlexarrClient.requestDownload(itemId: String, quality: DownloadQuality): OfflineDownload =
    sendDecoding("POST", "offline", DownloadRequestBody(itemId, quality))

suspend fun FlexarrClient.offlineDownload(id: String): OfflineDownload = get("offline/$id")

/** The phone has the file: the server's copy can go. */
suspend fun FlexarrClient.markDownloadReceived(id: String) {
    sendRaw("POST", "offline/$id/received")
}

/** Taken off the phone (a podcast episode's is remembered, so it isn't offered to download by itself again). */
suspend fun FlexarrClient.deleteDownload(id: String) {
    sendRaw("DELETE", "offline/$id")
}

/** Watched with no connection: recorded now, unless something newer was since. */
suspend fun FlexarrClient.reportOfflineProgress(itemId: String, positionSeconds: Double, finished: Boolean, playedAt: Instant) {
    send(
        "POST",
        "items/$itemId/offline-progress",
        body = FlexarrClient.json.encodeToString(OfflineProgressBody.serializer(), OfflineProgressBody(positionSeconds.coerceAtLeast(0.0), finished, playedAt.toString())),
    )
}

/** The newest unplayed episodes of the shows this profile auto-downloads (none when its Auto-download is off). */
suspend fun FlexarrClient.podcastAutoDownloads(): List<PodcastAutoDownload> = get("podcasts/auto-downloads")

/** The request for a ready download's file, signed in, from [fromByte] on (to carry on a transfer that stopped). */
fun FlexarrClient.downloadFileRequest(id: String, fromByte: Long = 0): Request =
    Request.Builder()
        .url(baseUrl.newBuilder().addPathSegments("api/v1/offline/$id/file").build())
        .signIn()
        .apply { if (fromByte > 0) header("Range", "bytes=$fromByte-") }
        .build()

// On the phone

@Serializable
enum class LocalPhase {
    /** The server is getting it ready ([LocalDownload.progress], 0 to 1). */
    Preparing,
    /** Coming down to the phone ([LocalDownload.progress], 0 to 1). */
    Downloading,
    Downloaded,
    /** [LocalDownload.failure] says why. */
    Failed,
}

/** Watched offline and not yet told to the server. */
@Serializable
data class UnsentProgress(val position: Double, val finished: Boolean, val playedAtMillis: Long)

/** A film, episode or podcast episode kept on this phone for watching or listening with no connection. Saved as JSON. */
@Serializable
data class LocalDownload(
    /** The server's id for it. */
    val id: String,
    /** Whose it is: each profile sees only its own. */
    val profileId: String,
    val quality: DownloadQuality,
    val item: ItemSummary,
    val markers: List<Marker> = emptyList(),
    val durationSeconds: Double = 0.0,
    val nextId: String? = null,
    val phase: LocalPhase = LocalPhase.Preparing,
    val progress: Double = 0.0,
    val failure: String? = null,
    val sizeBytes: Long? = null,
    /** In the downloads folder, once it's here. */
    val fileName: String? = null,
    /** What the server will send it as ("….mp3", say): the file is kept under that extension. */
    val remoteFileName: String? = null,
    /** Its picture: a film's backdrop, an episode's still, a podcast episode's artwork. */
    val imageFileName: String? = null,
    /** Where watching got to, and when it was finished (an episode is deleted a day after). */
    val position: Double = 0.0,
    val finishedAtMillis: Long? = null,
    val unsent: UnsentProgress? = null,
    /** Fetched by itself: as the next episode, or a podcast's newest. Only these wait for Wi-Fi. */
    val automatic: Boolean = false,
    val createdAtMillis: Long = 0,
) {
    val isDownloaded: Boolean get() = phase == LocalPhase.Downloaded && fileName != null

    val isPodcastEpisode: Boolean get() = item.kind == MediaKind.PodcastEpisode

    /** Still to come: being got ready or coming down. */
    val isPending: Boolean get() = phase == LocalPhase.Preparing || phase == LocalPhase.Downloading

    /** The extension to keep the file under, so the player opens it: the server's, or MP4. */
    val fileExtension: String
        get() = remoteFileName?.substringAfterLast('.', "")?.takeIf { it.isNotEmpty() && it.length <= 5 } ?: "mp4"

    /** Where playing starts: where it was left (past the first half minute of a film or episode), or the beginning. */
    val startPosition: Double
        get() = when {
            finishedAtMillis != null -> 0.0
            isPodcastEpisode -> position
            position > 30 -> position
            else -> 0.0
        }

    /** Watched to [position] (or to the end): kept here, and to be told to the server. */
    fun recorded(position: Double, finished: Boolean, now: Long): LocalDownload = copy(
        position = if (finished) 0.0 else position,
        finishedAtMillis = if (finished) finishedAtMillis ?: now else if (position > 30) null else finishedAtMillis,
        unsent = UnsentProgress(position, finished, now),
    )
}

/** What a download does after hearing from the server. */
sealed interface ServerAnswer {
    /** Still being got ready, this far. */
    data class Preparing(val progress: Double) : ServerAnswer
    /** Ready: fetch the file. */
    data object Fetch : ServerAnswer
    /** Deleted on another of this profile's phones before it came: forget it. */
    data object Forget : ServerAnswer
    data class Failed(val message: String) : ServerAnswer
    /** Nothing changes (it's here already, or the status is from a newer server). */
    data object None : ServerAnswer
}

object DownloadRules {
    const val DAY_MILLIS = 24 * 60 * 60 * 1000L

    fun answer(local: LocalDownload, remote: OfflineDownload): ServerAnswer = when (remote.status) {
        DownloadStatus.Queued, DownloadStatus.Preparing -> ServerAnswer.Preparing(remote.progress)
        DownloadStatus.Ready -> if (local.isDownloaded) ServerAnswer.None else ServerAnswer.Fetch
        DownloadStatus.Removed -> if (local.isDownloaded) ServerAnswer.None else ServerAnswer.Forget
        DownloadStatus.Received, DownloadStatus.Expired ->
            if (local.isDownloaded) ServerAnswer.None else ServerAnswer.Failed("This download ran out on the server. Download it again.")
        DownloadStatus.Failed -> ServerAnswer.Failed(remote.error ?: "The server couldn’t get this ready.")
        DownloadStatus.Unknown -> ServerAnswer.None
    }

    /** Fetched by itself while automatic downloads are kept to Wi-Fi: it never comes over mobile data. */
    fun isWiFiOnly(download: LocalDownload, automaticOnWiFiOnly: Boolean): Boolean = automaticOnWiFiOnly && download.automatic

    /** Episodes and podcast episodes finished more than a day ago, with nothing left to tell the server (films stay until deleted). */
    fun finishedWith(downloads: List<LocalDownload>, now: Long): List<LocalDownload> = downloads.filter { download ->
        (download.item.kind == MediaKind.Episode || download.isPodcastEpisode) && download.unsent == null &&
            download.finishedAtMillis?.let { now - it > DAY_MILLIS } == true
    }

    /** The size to ask for: the one chosen, unless that's an Original this phone can't play (then High). */
    fun quality(chosen: DownloadQuality, options: List<DownloadOption>?): DownloadQuality =
        if (chosen == DownloadQuality.Original && options?.firstOrNull { it.quality == DownloadQuality.Original }?.available == false) DownloadQuality.High else chosen

    /** A finished episode's next one, to fetch by itself: an episode with a next, not already here or on its way. */
    fun nextToFetch(download: LocalDownload, downloads: List<LocalDownload>): String? {
        if (download.item.kind != MediaKind.Episode || !download.isDownloaded) return null
        val next = download.nextId ?: return null
        return next.takeIf { id -> downloads.none { it.profileId == download.profileId && it.item.id == id } }
    }
}

/** "1.2 GB", "340 MB", "12 KB": a file's size as people read it. */
fun Format.bytes(bytes: Long): String = when {
    bytes >= 1_000_000_000 -> String.format(java.util.Locale.getDefault(), "%.1f GB", bytes / 1_000_000_000.0)
    bytes >= 1_000_000 -> String.format(java.util.Locale.getDefault(), "%.0f MB", bytes / 1_000_000.0)
    else -> String.format(java.util.Locale.getDefault(), "%.0f KB", bytes / 1_000.0)
}

/** A downloaded podcast episode as the podcast player and its rows know one (a download keeps the episode as a title). */
fun ItemSummary.asPodcastEpisode(): PodcastEpisode = PodcastEpisode(
    id = id,
    podcastId = seriesId ?: parentId ?: "",
    podcastTitle = seriesTitle ?: "",
    title = title,
    publishedAt = airDate ?: addedAt,
    durationSeconds = durationSeconds,
    artwork = artwork,
    accentColor = accentColor,
    userData = userData,
)
