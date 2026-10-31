package io.github.flexarr.core

import kotlinx.serialization.EncodeDefault
import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.KSerializer
import kotlinx.serialization.Serializable
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder

// Kotlin mirrors of `frontend/src/api/types.ts`, as FlexarrKit's Models.swift is for the Apple apps. JSON is camelCase,
// enums are strings and timestamps ISO 8601 (kept as strings until something needs them as times). Only what the app
// uses is here: the decoder ignores the rest. A field newer than the oldest server the app supports is nullable with
// a default, noted "Absent from servers before …".

// Enums

/** A string enum that decodes values from a newer server as its `Unknown` instead of failing. */
interface ForwardCompatibleEnum {
    val raw: String
}

open class ForwardCompatibleEnumSerializer<T>(
    name: String,
    private val values: List<T>,
    private val unknown: T,
) : KSerializer<T> where T : Enum<T>, T : ForwardCompatibleEnum {
    override val descriptor = PrimitiveSerialDescriptor("io.github.flexarr.$name", PrimitiveKind.STRING)
    override fun deserialize(decoder: Decoder): T = decoder.decodeString().let { raw -> values.firstOrNull { it.raw == raw } ?: unknown }
    override fun serialize(encoder: Encoder, value: T) = encoder.encodeString(value.raw)
}

@Serializable(with = LibraryType.Serializer::class)
enum class LibraryType(override val raw: String) : ForwardCompatibleEnum {
    Movies("Movies"), TvShows("TvShows"), Music("Music"), Podcasts("Podcasts"), HomeVideos("HomeVideos"), Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<LibraryType>("LibraryType", entries, Unknown)
}

@Serializable(with = MediaKind.Serializer::class)
enum class MediaKind(override val raw: String) : ForwardCompatibleEnum {
    Movie("Movie"), Series("Series"), Season("Season"), Episode("Episode"),
    Artist("Artist"), Album("Album"), Track("Track"),
    Podcast("Podcast"), PodcastEpisode("PodcastEpisode"), Unknown("");

    /** Something that plays: a film, an episode or a track (the rest open to their contents). */
    val isPlayable: Boolean get() = this == Movie || this == Episode || this == Track || this == PodcastEpisode

    /** An artist, album or track: listened to, not watched, so the music screens and player take it. */
    val isMusic: Boolean get() = this == Artist || this == Album || this == Track

    object Serializer : ForwardCompatibleEnumSerializer<MediaKind>("MediaKind", entries, Unknown)
}

@Serializable(with = PlaybackMethod.Serializer::class)
enum class PlaybackMethod(override val raw: String) : ForwardCompatibleEnum {
    DirectPlay("DirectPlay"), Transcode("Transcode"), Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<PlaybackMethod>("PlaybackMethod", entries, Unknown)
}

@Serializable(with = MarkerType.Serializer::class)
enum class MarkerType(override val raw: String) : ForwardCompatibleEnum {
    Intro("Intro"), Recap("Recap"), Credits("Credits"), Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<MarkerType>("MarkerType", entries, Unknown)
}

@Serializable(with = HomeRowStyle.Serializer::class)
enum class HomeRowStyle(override val raw: String) : ForwardCompatibleEnum {
    Poster("poster"), Wide("wide"), Top10("top10"),
    /** The items are tracks (most played, say): shown as tracks that play, not titles that open. */
    Tracks("tracks"),
    /** The profile's playlists; `items` is empty. */
    Playlists("playlists"),
    /** Favourite TV channels; `items` is empty. */
    Live("live"),
    Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<HomeRowStyle>("HomeRowStyle", entries, Unknown)
}

// System

@Serializable
data class SystemStatus(
    val appName: String,
    val version: String,
    val osDescription: String = "",
    val startTime: String? = null,
    /** The server's address for away from home (over Tailscale), once remote access is turned on. */
    val remoteUrl: String? = null,
    /** Whether the server's owner has turned on error reports (to Sentry) for the apps. Absent from older servers: off. */
    val errorReports: ErrorReportSetting? = null,
    /**
     * The server's own random id, which [FlexarrClient.serverIdAt] asks another address for before the app sends its
     * sign-in there. Absent from servers before 0.55.
     */
    val serverId: String? = null,
)

/** The server's error-report switch, which the apps follow: off unless the owner turned it on. */
@Serializable
data class ErrorReportSetting(
    val enabled: Boolean,
    /** A random id for the server, not tied to anything else: the only "user" an error report carries. */
    val installId: String? = null,
)

// Libraries

@Serializable
data class Library(
    val id: String,
    val name: String,
    val type: LibraryType,
    val folders: List<String> = emptyList(),
    val itemCount: Int,
    /** A scan under way. */
    val scan: ScanStatus? = null,
)

@Serializable
data class ScanStatus(val path: String, val total: Int, val processed: Int)

@Serializable
data class Paged<T>(
    val total: Int,
    val items: List<T>,
)

/** Sort orders accepted by `GET /libraries/{id}/items`; `raw` is the query value (none for title). */
enum class LibrarySort(val raw: String, val label: String) {
    Title("", "Title"),
    Added("added", "Recently added"),
    Year("year", "Year"),
    Rating("rating", "Rating"),
}

// Items

@Serializable
data class Artwork(
    val poster: String? = null,
    val backdrop: String? = null,
    val logo: String? = null,
    val thumb: String? = null,
)

@Serializable
data class UserData(
    val positionSeconds: Double,
    val watched: Boolean,
    val playCount: Int,
    val unwatchedCount: Int? = null,
)

@Serializable
data class ItemSummary(
    val id: String,
    val libraryId: String,
    val parentId: String? = null,
    val kind: MediaKind,
    val title: String,
    val year: Int? = null,
    val seasonNumber: Int? = null,
    val episodeNumber: Int? = null,
    val episodeNumberEnd: Int? = null,
    val airDate: String? = null,
    val childCount: Int,
    val durationSeconds: Double? = null,
    val resolution: String? = null,
    val artwork: Artwork,
    val accentColor: String? = null,
    val communityRating: Double? = null,
    val contentRating: String? = null,
    val runtimeMinutes: Int? = null,
    val releaseDate: String? = null,
    val userData: UserData,
    /** The show for episodes and seasons; the artist for tracks and albums. */
    val seriesTitle: String? = null,
    /** The show's id for episodes and seasons. Absent from servers before it existed. */
    val seriesId: String? = null,
    /** In a home video library, everything is known by name rather than number. Absent from servers before home videos. */
    val homeVideo: Boolean? = null,
    /** When it came into the library (ISO 8601, no zone). */
    val addedAt: String? = null,
    /** A track's disc and its place on it. */
    val discNumber: Int? = null,
    val trackNumber: Int? = null,
    /** Tracks only: the track's own artist when it isn't the album's (a compilation's tracks, a featured artist). */
    val trackArtist: String? = null,
)

@Serializable(with = VideoRange.Serializer::class)
enum class VideoRange(override val raw: String, val label: String) : ForwardCompatibleEnum {
    Sdr("Sdr", "SDR"), Hdr10("Hdr10", "HDR10"), Hlg("Hlg", "HLG"), DolbyVision("DolbyVision", "Dolby Vision"), Unknown("", "");

    object Serializer : ForwardCompatibleEnumSerializer<VideoRange>("VideoRange", entries, Unknown)
}

@Serializable
data class MediaFile(
    val id: String,
    val size: Long,
    val videoCodec: String? = null,
    val height: Int? = null,
    val videoRange: VideoRange = VideoRange.Unknown,
    val audioCodec: String? = null,
    val audioChannels: Int? = null,
    /** HDR10+ scene-by-scene metadata on top of the HDR10. Absent from servers before 0.53. */
    val hdr10Plus: Boolean? = null,
) {
    /** "HDR10+", "Dolby Vision, HDR10+"; null for SDR. */
    val pictureLabel: String?
        get() {
            if (videoRange == VideoRange.Sdr || videoRange == VideoRange.Unknown) return null
            if (hdr10Plus != true) return videoRange.label
            return if (videoRange == VideoRange.DolbyVision) "Dolby Vision, HDR10+" else "HDR10+"
        }
}

@Serializable(with = CreditRole.Serializer::class)
enum class CreditRole(override val raw: String) : ForwardCompatibleEnum {
    Actor("Actor"), Director("Director"), Writer("Writer"), Producer("Producer"), Creator("Creator"), Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<CreditRole>("CreditRole", entries, Unknown)
}

@Serializable
data class Credit(
    val personId: String,
    val name: String,
    val role: CreditRole,
    val character: String? = null,
    val image: String? = null,
)

@Serializable
data class GenreCount(
    val name: String,
    val count: Int,
    /** A backdrop from one of the library's titles in the genre, for its tile. Absent from servers before 0.54. */
    val backdrop: String? = null,
)

@Serializable
data class ItemDetail(
    val item: ItemSummary,
    val overview: String? = null,
    val studio: String? = null,
    val genres: List<String> = emptyList(),
    val ancestors: List<ItemSummary> = emptyList(),
    val children: List<ItemSummary> = emptyList(),
    val credits: List<Credit> = emptyList(),
    val files: List<MediaFile> = emptyList(),
    /** Overviews of `children`, keyed by child id (seasons carry their episodes' overviews here). */
    val childOverviews: Map<String, String?> = emptyMap(),
    /**
     * Subtitles can be found for it through the server's Bazarr (a film or episode Sonarr or Radarr manage, with a
     * file), for `files.first()`, the copy playback picks. Absent from servers before 0.53.
     */
    val canSearchSubtitles: Boolean? = null,
    /** Films and shows: whether it's on this profile's watchlist. Absent from servers before 0.56 (no watchlist). */
    val onWatchlist: Boolean? = null,
)

// People and search

@Serializable
data class PersonCredit(val item: ItemSummary, val role: CreditRole, val character: String? = null)

@Serializable
data class Person(val id: String, val name: String, val image: String? = null, val credits: List<PersonCredit> = emptyList())

@Serializable
data class PersonSummary(val id: String, val name: String, val image: String? = null, val credits: Int)

@Serializable
data class SearchResults(
    val titles: List<ItemSummary> = emptyList(),
    val episodes: List<ItemSummary> = emptyList(),
    val people: List<PersonSummary> = emptyList(),
    /** Albums by name (artists are among [titles]). Absent from servers before music search. */
    val albums: List<ItemSummary> = emptyList(),
    /** Tracks by name, each with its album's. Absent from servers before music search. */
    val tracks: List<SearchTrack> = emptyList(),
) {
    val isEmpty: Boolean get() = titles.isEmpty() && episodes.isEmpty() && people.isEmpty()
}

@Serializable
data class SearchTrack(val item: ItemSummary, val album: String? = null)

// Home

@Serializable
data class HomeRow(
    val id: String,
    val title: String,
    val style: HomeRowStyle,
    val items: List<ItemSummary>,
    val link: String? = null,
    /** A `live` row's channels (`items` is empty then). Absent from servers before Live TV. */
    val channels: List<LiveChannel>? = null,
    /** A `playlists` row's content (`items` is empty then). Absent from servers before playlists. */
    val playlists: List<PlaylistSummary>? = null,
)

@Serializable
data class Home(
    val featured: List<ItemSummary>,
    val overviews: Map<String, String?> = emptyMap(),
    val rows: List<HomeRow>,
    /** What the first featured title resumes (the episode, for a show), when it's from Continue Watching. */
    val resume: ItemSummary? = null,
    /** What each part-played featured title resumes, by its id. Absent from servers before 0.53. */
    val resumes: Map<String, ItemSummary>? = null,
) {
    /**
     * What a featured title's Play picks up from, when it's been started: the film itself, or a show's episode. A
     * server from before [resumes] says so only for the first.
     */
    fun resuming(item: ItemSummary): ItemSummary? {
        resumes?.let { return it[item.id] }
        return if (item.id == featured.firstOrNull()?.id) resume else null
    }
}

// Playback

@Serializable
data class DeviceProfile(
    val client: String,
    val containers: List<String>,
    val videoCodecs: List<String>,
    val audioCodecs: List<String>,
    val hdr: Boolean,
    val maxHeight: Int,
    val maxAudioChannels: Int,
    val maxBitrate: Long,
    /** Converted streams come as HLS in fragmented MP4, which carries FLAC and Dolby Atmos. */
    val fmp4: Boolean? = null,
    val dolbyVision: Boolean? = null,
    val hdr10: Boolean? = null,
    val hlg: Boolean? = null,
)

@Serializable
data class PlaybackRequest(
    val itemId: String,
    val profile: DeviceProfile,
    /** Omit to let the server pick the resume point; 0 starts from the beginning. */
    val startPositionSeconds: Double? = null,
    /** This player's previous session, which the new one takes over from. */
    val replacesSessionId: String? = null,
    /** With a show's id, a random episode starts; with an episode's, its `next` is a random one. */
    val shuffle: Boolean? = null,
    /** The file the session played, when opening the same title again (another audio track, moved subtitles). */
    val fileId: String? = null,
    val audioStreamIndex: Int? = null,
    /** A subtitle track's key, or "off"; null lets the server choose by the profile's preferences. */
    val subtitleKey: String? = null,
    /** Convert it even though the device says it can play the file (it turned out it couldn't). */
    val forceTranscode: Boolean? = null,
    /** Getting the next episode ready during the credits: conversion starts now, and it never ends another screen. */
    val prepare: Boolean? = null,
)

@Serializable
data class AudioTrack(
    val index: Int,
    val label: String,
    val codec: String,
    val language: String? = null,
    val channels: Int? = null,
    val isDefault: Boolean = false,
)

@Serializable
data class SubtitleTrack(
    val key: String,
    val label: String,
    val codec: String,
    val language: String? = null,
    val isForced: Boolean = false,
    val isDefault: Boolean = false,
    /** Text the player draws itself (from [url], WebVTT); picture-based ones have to be burned into the picture. */
    val isText: Boolean,
    val url: String? = null,
    /** A subtitle file beside the video (rather than one inside it): only these can be re-synced. Absent from servers before 0.53. */
    val isExternal: Boolean? = null,
    /**
     * Styled (ASS/SSA) subtitles as authored, for a player that draws their places and colours itself. Relative to the
     * server, like [url]; not shifted by the title's subtitle delay. Absent from servers before 0.53, and for plain text.
     */
    val styledUrl: String? = null,
) {
    /** DVB teletext, as European broadcasts carry subtitles: the page they're read from can be chosen. */
    val isTeletext: Boolean get() = codec == "dvb_teletext"
}

/** Teletext subtitles, which the server converts to WebVTT (Media3 can't read teletext itself). */
object Teletext {
    /**
     * The caption pages broadcasters use most, as the iPhone offers them: 888 (the UK, Ireland, the Netherlands), 777
     * and 150 (Germany and elsewhere), 801 (Sweden).
     */
    val pages = listOf(888, 777, 150, 801)

    /** Servers from 0.55 read a chosen page; older ones only the pages the broadcast marks as subtitles. */
    fun canChoosePage(serverVersion: String?): Boolean = serverVersion != null && ServerVersion.atLeast(serverVersion, 0, 55)

    /** [url] (a track's WebVTT) read from [page] instead, or as it is with none. */
    fun url(url: String, page: Int?): String =
        if (page == null) url else url + (if ('?' in url) '&' else '?') + "page=$page"
}

@Serializable
data class Marker(
    val type: MarkerType,
    val start: Double,
    val end: Double,
)

@Serializable
data class PlaybackInfo(
    val sessionId: String,
    val method: PlaybackMethod,
    /** Relative to the server; resolve with `FlexarrClient.url`. Needs no API key. */
    val url: String,
    val durationSeconds: Double,
    /** Where to start: the resume point, or what the request asked for. */
    val startPositionSeconds: Double,
    val markers: List<Marker> = emptyList(),
    val item: ItemSummary,
    val ancestors: List<ItemSummary> = emptyList(),
    val next: ItemSummary? = null,
    /** The episode before this one, in order: null on shuffle, for the first episode, and from servers before it. */
    val previous: ItemSummary? = null,
    val transcodeSummary: String? = null,
    /** The profile's auto-play and skip preferences. Absent from servers before they existed. */
    val preferences: PlaybackPreferences? = null,
    /** A short hash of `sessionId`, for logs. Absent from older servers. */
    val playbackId: String? = null,
    val fileId: String? = null,
    val audioTracks: List<AudioTrack> = emptyList(),
    val selectedAudioIndex: Int? = null,
    val subtitles: List<SubtitleTrack> = emptyList(),
    /** What the server chose to show: the profile's preference, or the viewer's last pick for this title or show. */
    val selectedSubtitleKey: String? = null,
    /** The chosen subtitles are drawn into the picture by the server (picture-based ones). */
    val subtitleBurnedIn: Boolean = false,
    /** This title's subtitles are shown this many seconds later (earlier when negative). The server shifts them. */
    val subtitleDelaySeconds: Double? = null,
    /**
     * The file's sound is played this many seconds later (earlier when negative), for the whole household. Absent from
     * servers before 0.53, which can't move it.
     */
    val audioDelaySeconds: Double? = null,
    /** The server's stream already has the sound moved by [audioDelaySeconds]. Absent from servers before 0.53. */
    val audioDelayInStream: Boolean? = null,
    /** The speed this profile watches the show at (null: normal). Absent from servers before 0.53. */
    val speed: Double? = null,
    /**
     * The server can find subtitles for this title (through Bazarr), and re-sync the ones beside its file. Absent from
     * servers before 0.53.
     */
    val canSearchSubtitles: Boolean? = null,
) {
    /** The server keeps audio offsets and show speeds (0.53 and later). */
    val hasPlayerControls: Boolean get() = audioDelaySeconds != null
}

/**
 * How a profile likes to watch: auto-play the next episode, and what to skip without asking. The four switches are
 * always sent, even at their defaults: the server reads one that's missing as off.
 */
@OptIn(ExperimentalSerializationApi::class)
@Serializable
data class PlaybackPreferences(
    @EncodeDefault val autoPlayNext: Boolean = true,
    @EncodeDefault val skipIntros: Boolean = false,
    @EncodeDefault val skipRecaps: Boolean = false,
    @EncodeDefault val skipCredits: Boolean = false,
    /** Absent from servers before subtitle sizes; left out when saving, the server keeps the profile's own. */
    val subtitleSize: SubtitleSize? = null,
    /** How high subtitles sit over the picture. Absent from servers before 0.53; left out when saving, it's kept. */
    val subtitlePosition: SubtitlePosition? = null,
    /** In films wider than the screen, subtitles go in the black bar below the picture. Absent from servers before 0.53. */
    val subtitlesInBlackBars: Boolean? = null,
)

@Serializable(with = SubtitleSize.Serializer::class)
enum class SubtitleSize(override val raw: String, val label: String, val percent: Int) : ForwardCompatibleEnum {
    Small("Small", "Small", 80), Standard("Standard", "Standard", 100), Large("Large", "Large", 125),
    ExtraLarge("ExtraLarge", "Extra large", 150), Unknown("", "Standard", 100);

    object Serializer : ForwardCompatibleEnumSerializer<SubtitleSize>("SubtitleSize", entries, Unknown)

    companion object {
        val choices = listOf(Small, Standard, Large, ExtraLarge)
    }
}

@Serializable(with = SubtitlePosition.Serializer::class)
enum class SubtitlePosition(override val raw: String, val label: String) : ForwardCompatibleEnum {
    Bottom("Bottom", "Bottom"), Raised("Raised", "Raised"), Higher("Higher", "Higher"), Top("Top", "Top"), Unknown("", "Bottom");

    /**
     * How far above the picture's foot the bottom of a subtitle sits, as a share of the picture's height (as the web
     * player places them); null at the top.
     */
    val lift: Float?
        get() = when (this) {
            Bottom, Unknown -> 0.08f
            Raised -> 0.16f
            Higher -> 0.26f
            Top -> null
        }

    object Serializer : ForwardCompatibleEnumSerializer<SubtitlePosition>("SubtitlePosition", entries, Unknown)

    companion object {
        val choices = listOf(Bottom, Raised, Higher, Top)
    }
}

@Serializable
internal data class SubtitleChoice(val key: String)

@Serializable
internal data class SecondsBody(val seconds: Double)

@Serializable
internal data class SpeedBody(val speed: Double)

@Serializable
internal data class ProgressReport(val positionSeconds: Double, val paused: Boolean, val ended: Boolean)

@Serializable
internal data class WatchedRequest(val watched: Boolean)

// Pairing and sign-in

/** A code for connecting this device, shown on screen for someone to approve. */
@Serializable
data class PairingStart(
    /** The device's secret handle for polling; never shown. */
    val id: String,
    /** What a person approves, e.g. "K7M-Q4X". */
    val code: String,
    val expiresAt: String,
)

/** GET /setup: a new server that nobody has set up in a browser yet. */
@Serializable
data class SetupStatus(
    val open: Boolean,
    /** Setting up asks for the code in the server's log. Absent from servers before 0.52. */
    val codeRequired: Boolean? = null,
    /** The server's id, as [SystemStatus.serverId]; anyone may ask for it. Absent from servers before 0.55. */
    val serverId: String? = null,
)

@Serializable
data class PairingStatus(
    /** "waiting", "approved" or "expired". */
    val status: String,
    /** Set once, when approved: this device's own token, sent as the API key from then on. */
    val apiKey: String? = null,
    /** With "expired": the main profile chose "Don't allow". Absent from servers before 0.56. */
    val declined: Boolean = false,
)

@Serializable
internal data class PairingStartBody(val deviceName: String, val stopsWhenDeclined: Boolean)

/** What `/me` says about this client. */
@Serializable
data class SignInInfo(
    val profileId: String,
    /** "api-key" (the server's key itself), "device" (this device's own token), "tailscale" or "local". */
    val method: String,
    /** May manage the server now: the API key, or the main profile on a sign-in where its PIN was given lately. */
    val isAdmin: Boolean,
    /** The main profile is chosen, but managing the server takes its PIN first ([FlexarrClient.unlockAdmin]). Absent before 0.56. */
    val canUnlock: Boolean = false,
    /** The main profile has no PIN yet, so managing the server can't be unlocked until it's given one in a browser. Absent before 0.56. */
    val adminPinMissing: Boolean = false,
    /** When managing the server takes the PIN again on this sign-in; null for the API key, or when it isn't unlocked. */
    val unlockedUntil: String? = null,
    /** The profile is a child's: the kids app. Absent from servers before 0.56. */
    val isKids: Boolean = false,
    /** The profile's age limit on the UK's steps (0 is U, 8 PG, then 12, 15, 18); null, none. Absent from servers before 0.56. */
    val maxRatingAge: Int? = null,
    /** This device is locked to kids profiles until the admin PIN releases it. Absent from servers before 0.56. */
    val kidsLock: Boolean = false,
    /** A family guest's device: it stays on its invite's profile and has no household devices. Absent from servers before 0.57. */
    val isGuest: Boolean = false,
) {
    /** The main profile, unlocked or not: it's asked to let devices in (allowing takes its PIN while locked). */
    val isMainProfile: Boolean get() = isAdmin || canUnlock
}

@Serializable
internal data class AdminUnlockRequest(val pin: String)

/** What unlocking says: until when, or null for the API key, which always may. */
@Serializable
data class AdminUnlock(val unlockedUntil: String? = null)

@Serializable
internal data class DeviceToken(val token: String)

// Profiles

/** A person in the household: their own watch history, Continue Watching and (optionally) a PIN. */
@Serializable
data class Profile(
    val id: String,
    val name: String,
    /** "#a9c2ec"; the circle behind the initial when there's no picture. */
    val color: String,
    val hasPin: Boolean = false,
    val isMain: Boolean = false,
    /**
     * Choosing it here asks for a PIN: its own, or for the main profile where the owner didn't sign in, the admin PIN
     * (see [asksAdminPin]). Absent from servers before 0.56, where [hasPin] says.
     */
    val asksPin: Boolean? = null,
    /** The PIN choosing it here asks for is the admin PIN, not its own. Absent from servers before 0.57: see [takesAdminPin]. */
    val asksAdminPin: Boolean? = null,
    /** The profile the request that fetched this list acted for. */
    val isCurrent: Boolean = false,
    /** The picture's server-relative URL, or null to show the initial on the colour. */
    val image: String? = null,
    /**
     * The order of the libraries in every app's menu: library ids, and "live-tv" for apps that list Live TV among them.
     * Empty: by name. Absent from servers before 0.54, which can't save one.
     */
    val libraryOrder: List<String>? = null,
    /** Its age limit on the UK's steps (0 is U, 8 PG, then 12, 15, 18); null, none. Absent from servers before 0.56. */
    val maxRatingAge: Int? = null,
    /** A child's profile: the kids app, and leaving it takes the admin PIN. Absent from servers before 0.56. */
    val isKids: Boolean = false,
) {
    val initial: String get() = name.trim().firstOrNull()?.uppercase() ?: "?"

    /** Has an age limit: Discover, Coming Soon and requests aren't for it, since their titles have no age to check. */
    val hasAgeLimit: Boolean get() = maxRatingAge != null

    /** Whether choosing it here takes its PIN. */
    val takesPin: Boolean get() = asksPin ?: hasPin

    /**
     * Whether the PIN choosing it here takes is the admin PIN: as the server says, or on older servers for a main
     * profile with no PIN of its own (the only one there it could be).
     */
    val takesAdminPin: Boolean get() = asksAdminPin ?: (isMain && !hasPin)
}

/**
 * The order to save after the libraries [shown] here were rearranged: [saved] with the shown ones' places refilled in
 * their new order, so what isn't shown here ("live-tv", libraries this app leaves out) keeps its place among them. Shown
 * ones the saved order didn't mention yet go at the end.
 */
fun libraryOrder(saved: List<String>, shown: List<String>): List<String> {
    val isShown = shown.map { it.lowercase() }.toSet()
    val next = shown.iterator()
    val order = saved.map { if (it.lowercase() in isShown && next.hasNext()) next.next() else it }
    return order + next.asSequence().toList()
}

/** Live TV's place in a profile's library order. */
const val LIVE_TV_ORDER_ID = "live-tv"

/**
 * [entries] in the order of [saved] (ids compared ignoring case); any it doesn't mention, such as a new library, after
 * them as they came. As FlexarrKit's LibraryOrder.sorted.
 */
fun <T> sortedByLibraryOrder(entries: List<T>, saved: List<String>, id: (T) -> String): List<T> {
    val rank = HashMap<String, Int>()
    saved.forEachIndexed { index, entry -> rank.putIfAbsent(entry.lowercase(), index) }
    return entries.withIndex().sortedBy { (index, entry) -> rank[id(entry).lowercase()] ?: (saved.size + index) }.map { it.value }
}

/** What choosing a profile returns: the token to send as `X-Flexarr-Profile` from then on. */
@Serializable
data class ProfileSelection(val token: String, val profile: Profile)

@Serializable
internal data class SelectProfileRequest(val pin: String?, val parentPin: String? = null)

@Serializable
internal data class KidsLockRequest(val pin: String?, val profileId: String? = null)

@Serializable
internal data class ForgotPinRequest(val adminPin: String)

@Serializable
internal data class LibraryOrderRequest(val order: List<String>)
