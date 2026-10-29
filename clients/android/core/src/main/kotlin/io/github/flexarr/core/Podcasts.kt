package io.github.flexarr.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.serializer

// Podcasts, as FlexarrKit's Podcasts.swift: the shows a profile follows, their episodes, Up Next, and Apple's
// directory for finding more. Times are ISO 8601 strings.

/**
 * Whether this profile has podcasts. Every server has one Podcasts library, so [available] is false only when it's
 * hidden from this profile. [country] is whose charts Browse shows.
 */
@Serializable
data class PodcastStatus(val available: Boolean, val country: String = "")

/** This profile follows the show (since when), and whether it listens from the first episode on. */
@Serializable
data class PodcastSubscription(
    val subscribedAt: String,
    /** Episodes in the order they came out: the show lists and plays its oldest unplayed ones first. */
    val oldestFirst: Boolean? = null,
)

/** How a profile listens to podcasts, on every device: downloads on phones, and the speed episodes play at. */
@Serializable
data class PodcastPreferences(
    val autoDownload: Boolean = true,
    val keepEpisodes: Int = 3,
    val speed: Double = 1.0,
)

/** Changes to the podcast preferences; null leaves one as it is. */
@Serializable
data class PodcastPreferencesUpdate(
    val autoDownload: Boolean? = null,
    val keepEpisodes: Int? = null,
    val speed: Double? = null,
)

@Serializable
data class Podcast(
    val id: String,
    val title: String,
    val author: String? = null,
    val overview: String? = null,
    val artwork: Artwork = Artwork(),
    val accentColor: String? = null,
    /** The show's own website, from its feed. */
    val link: String? = null,
    val categories: List<String> = emptyList(),
    val explicit: Boolean = false,
    val episodeCount: Int = 0,
    val unplayedCount: Int = 0,
    val latestEpisodeAt: String? = null,
    /** Why the feed couldn't be fetched last time, in words for the listener. */
    val error: String? = null,
    /** Null when this profile doesn't follow it. */
    val subscription: PodcastSubscription? = null,
) {
    val isSubscribed: Boolean get() = subscription != null

    /** "3 unplayed", "Up to date", "1,204 episodes": what a show's card says under its name. */
    val countLine: String
        get() = if (isSubscribed) {
            if (unplayedCount == 0) "Up to date" else "${Format.count(unplayedCount)} unplayed"
        } else {
            if (episodeCount == 1) "1 episode" else "${Format.count(episodeCount)} episodes"
        }
}

@Serializable(with = PodcastEpisodeType.Serializer::class)
enum class PodcastEpisodeType(override val raw: String) : ForwardCompatibleEnum {
    Full("Full"), Trailer("Trailer"), Bonus("Bonus"), Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<PodcastEpisodeType>("PodcastEpisodeType", entries, Unknown)
}

@Serializable
data class PodcastEpisode(
    val id: String,
    val podcastId: String,
    val podcastTitle: String,
    val title: String,
    /** The episode's notes as plain text: cut short in lists, whole from `podcastEpisode`. */
    val overview: String? = null,
    val publishedAt: String? = null,
    val durationSeconds: Double? = null,
    val season: Int? = null,
    val episode: Int? = null,
    val episodeType: PodcastEpisodeType = PodcastEpisodeType.Unknown,
    val explicit: Boolean = false,
    val artwork: Artwork = Artwork(),
    val accentColor: String? = null,
    /** `watched` means played. */
    val userData: UserData,
    val hasTranscript: Boolean = false,
) {
    val isPlayed: Boolean get() = userData.watched

    /** Listened to part of the way (and not finished). */
    val progress: Double?
        get() {
            val duration = durationSeconds ?: return null
            if (userData.watched || userData.positionSeconds <= 0 || duration <= 0) return null
            return (userData.positionSeconds / duration).coerceAtMost(1.0)
        }

    /** The same episode marked played (from the start) or unplayed: shown before the server confirms it. */
    fun withPlayed(played: Boolean): PodcastEpisode =
        copy(userData = userData.copy(positionSeconds = 0.0, watched = played, playCount = if (played) maxOf(1, userData.playCount) else userData.playCount))

    /** "1h 6m", or "24m left" part way through, or "Played". */
    val timeLine: String?
        get() {
            if (isPlayed) return "Played"
            val duration = durationSeconds ?: return null
            if (userData.positionSeconds > 0) return Format.remaining(duration - userData.positionSeconds)
            return Format.runtime(duration)
        }
}

/** Where a chapter of an episode starts, and what it's called. */
@Serializable
data class PodcastChapter(val startSeconds: Double, val title: String)

/** Where the chapter playing at [position] is in the list; null before the first starts. */
fun List<PodcastChapter>.indexAt(position: Double): Int? =
    indexOfLast { it.startSeconds <= position + 0.5 }.takeIf { it >= 0 }

/** Something to tap in an episode's notes, over [range] of the text: a time to jump to, or a web address. */
sealed interface NoteLink {
    val range: IntRange

    data class Seek(override val range: IntRange, val seconds: Double) : NoteLink

    data class Web(override val range: IntRange, val url: String) : NoteLink
}

/**
 * The times ("12:34", "1:02:55") in an episode's notes that jump there (only those within the episode, when its
 * length is known), and the http(s) addresses that open, in the order they come.
 */
fun noteLinks(notes: String, durationSeconds: Double?): List<NoteLink> {
    val web = webPattern.findAll(notes).map { match ->
        // A full stop or bracket closing the sentence isn't part of the address.
        val url = match.value.trimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'', '"')
        NoteLink.Web(match.range.first until match.range.first + url.length, url)
    }.toList()
    val times = timePattern.findAll(notes).mapNotNull { match ->
        val seconds = match.value.split(":").fold(0.0) { total, part -> total * 60 + part.toInt() }
        if (durationSeconds != null && seconds >= durationSeconds) return@mapNotNull null
        // A time inside an address (a YouTube link's t=1:02, say) is the address's.
        if (web.any { match.range.first in it.range }) return@mapNotNull null
        NoteLink.Seek(match.range, seconds)
    }
    return (web + times).sortedBy { it.range.first }
}

/** A time on its own ("12:34", "1:02:55"), not part of a longer run of digits and colons. */
private val timePattern = Regex("""(?<![\d:])(?:\d{1,2}:)?\d{1,2}:[0-5]\d(?![\d:])""")

private val webPattern = Regex("""https?://[^\s<>"]+""", RegexOption.IGNORE_CASE)

/** A show in Apple's podcast directory (search and the charts), and whether it's in the library already. */
@Serializable
data class DirectoryPodcast(
    val iTunesId: Long,
    val title: String,
    val author: String? = null,
    /** Relative to the server, like other artwork. */
    val artwork: String? = null,
    val genre: String? = null,
    val explicit: Boolean = false,
    val episodeCount: Int? = null,
    /** Apple gives a feed for it, so it can be added. */
    val hasFeed: Boolean = true,
    val podcastId: String? = null,
    val subscribed: Boolean = false,
)

@Serializable
data class PodcastGenre(val id: Int, val name: String)

enum class PodcastEpisodeSort(val raw: String) { Newest("newest"), Oldest("oldest") }

@Serializable
internal data class UpNextBody(val episodeId: String, val next: Boolean)

@Serializable
internal data class UpNextOrderBody(val episodeIds: List<String>)

@Serializable
internal data class SubscriptionBody(val oldestFirst: Boolean? = null)

@Serializable
internal data class AddPodcastBody(val feedUrl: String? = null, val iTunesId: Long? = null)

/** Whether podcasts are set up for this profile. Servers before podcasts answer 404: none. */
suspend fun FlexarrClient.podcastStatus(): PodcastStatus? =
    try {
        get("podcasts/status")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) null else throw e
    }

/** The shows this profile follows, the most recently updated first. */
suspend fun FlexarrClient.podcasts(): List<Podcast> = get("podcasts")

suspend fun FlexarrClient.podcast(id: String): Podcast = get("podcasts/$id")

/** Adds a show to the library (or finds it there) by Apple's id or its feed's address. Doesn't follow it. */
suspend fun FlexarrClient.addPodcast(iTunesId: Long? = null, feedUrl: String? = null): Podcast =
    sendDecoding("POST", "podcasts", AddPodcastBody(feedUrl, iTunesId))

suspend fun FlexarrClient.podcastEpisodes(
    podcastId: String,
    sort: PodcastEpisodeSort = PodcastEpisodeSort.Newest,
    unplayed: Boolean = false,
    skip: Int = 0,
    take: Int = 50,
): Paged<PodcastEpisode> =
    get(
        "podcasts/$podcastId/episodes",
        buildMap {
            put("limit", take.toString())
            if (skip > 0) put("offset", skip.toString())
            if (sort != PodcastEpisodeSort.Newest) put("sort", sort.raw)
            if (unplayed) put("unplayed", "true")
        },
    )

/** One episode with all of its notes. */
suspend fun FlexarrClient.podcastEpisode(id: String): PodcastEpisode = get("podcasts/episodes/$id")

/** The episode's chapters, in order. Servers before chapters answer 404: none. */
suspend fun FlexarrClient.podcastChapters(episodeId: String): List<PodcastChapter> =
    try {
        get("podcasts/episodes/$episodeId/chapters")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) emptyList() else throw e
    }

/** The newest episodes of the shows this profile follows. */
suspend fun FlexarrClient.newPodcastEpisodes(unplayed: Boolean = false, take: Int = 40): List<PodcastEpisode> =
    get("podcasts/new", buildMap { put("limit", take.toString()); if (unplayed) put("unplayed", "true") })

/** Episodes this profile is part way through, the latest first. */
suspend fun FlexarrClient.continueListening(take: Int = 20): List<PodcastEpisode> = get("podcasts/continue", mapOf("limit" to take.toString()))

/** Follows a show, or (with [oldestFirst]) changes the order it's listened in. Returns the show with its subscription. */
suspend fun FlexarrClient.subscribe(podcastId: String, oldestFirst: Boolean? = null): Podcast =
    sendDecoding("PUT", "podcasts/$podcastId/subscription", SubscriptionBody(oldestFirst))

suspend fun FlexarrClient.unsubscribe(podcastId: String) {
    sendRaw("DELETE", "podcasts/$podcastId/subscription")
}

/**
 * What to play when this episode ends: the first in Up Next, else the next unplayed one of its show, else the newest
 * unplayed one of the shows followed. Null when there's nothing left (or the server is from before auto-play).
 */
suspend fun FlexarrClient.nextEpisode(after: String): PodcastEpisode? =
    try {
        val body = sendRaw("GET", "podcasts/episodes/$after/next")
        if (body.isEmpty()) null else decodeText(serializer(), body)
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) null else throw e
    }

/** The episodes to hear after this one, in order, the same on every device. Servers before Up Next have none. */
suspend fun FlexarrClient.upNext(): List<PodcastEpisode> =
    try {
        get("podcasts/queue")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) emptyList() else throw e
    }

/** Puts an episode in Up Next: first with [next], else last. One already there moves. Returns the queue. */
suspend fun FlexarrClient.addToUpNext(episodeId: String, next: Boolean): List<PodcastEpisode> =
    sendDecoding("POST", "podcasts/queue", UpNextBody(episodeId, next))

/** Up Next in this order; ids left out leave it. Returns the queue. */
suspend fun FlexarrClient.setUpNext(episodeIds: List<String>): List<PodcastEpisode> =
    sendDecoding("PUT", "podcasts/queue", UpNextOrderBody(episodeIds))

suspend fun FlexarrClient.removeFromUpNext(episodeId: String) {
    sendRaw("DELETE", "podcasts/queue/$episodeId")
}

suspend fun FlexarrClient.podcastPreferences(): PodcastPreferences = get("podcasts/preferences")

suspend fun FlexarrClient.setPodcastPreferences(update: PodcastPreferencesUpdate): PodcastPreferences =
    sendDecoding("PUT", "podcasts/preferences", update)

/** Fetches the show's feed again now. */
suspend fun FlexarrClient.refreshPodcast(id: String): Podcast = sendDecoding<Unit?, Podcast>("POST", "podcasts/$id/refresh", null)

/** Apple's directory, searched in the country chosen in Settings. */
suspend fun FlexarrClient.searchPodcasts(query: String): List<DirectoryPodcast> = get("podcasts/search", mapOf("q" to query))

/** The top shows, overall or in one of [podcastGenres]. */
suspend fun FlexarrClient.topPodcasts(genre: Int? = null): List<DirectoryPodcast> =
    get("podcasts/top", genre?.let { mapOf("genre" to it.toString()) } ?: emptyMap())

suspend fun FlexarrClient.podcastGenres(): List<PodcastGenre> = get("podcasts/genres")
