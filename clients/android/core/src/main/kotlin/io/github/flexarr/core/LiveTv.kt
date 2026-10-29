package io.github.flexarr.core

import kotlinx.serialization.KSerializer
import kotlinx.serialization.Serializable
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.serializer
import java.time.Instant
import java.time.OffsetDateTime

// Live TV from the server's Dispatcharr source: channels, the guide, watching, and the DVR. As FlexarrKit's LiveTv.swift.

/** An ISO 8601 time from the server ("2026-10-29T04:00:00Z", with or without fractions or an offset). */
object InstantSerializer : KSerializer<Instant> {
    override val descriptor = PrimitiveSerialDescriptor("io.github.flexarr.Instant", PrimitiveKind.STRING)
    override fun deserialize(decoder: Decoder): Instant = parseInstant(decoder.decodeString())
    override fun serialize(encoder: Encoder, value: Instant) = encoder.encodeString(value.toString())

    fun parseInstant(text: String): Instant = runCatching { Instant.parse(text) }.getOrElse { OffsetDateTime.parse(text).toInstant() }
}

/** Whether the server has a Live TV source (Dispatcharr), and how many channels it gave. */
@Serializable
data class LiveTvStatus(
    val configured: Boolean,
    val channels: Int,
    val refreshing: Boolean = false,
    /** Why the last refresh failed, in words. */
    val error: String? = null,
    /** How far back a channel being watched can be rewound. */
    val rewindMinutes: Int = 0,
)

/** A programme in the guide. */
@Serializable
data class LiveProgramme(
    val id: Long,
    @Serializable(with = InstantSerializer::class) val start: Instant,
    @Serializable(with = InstantSerializer::class) val end: Instant,
    val title: String,
    /** The episode's own title, when the guide gives one. */
    val subtitle: String? = null,
    val description: String? = null,
    val category: String? = null,
    val season: Int? = null,
    val episode: Int? = null,
    val isNew: Boolean = false,
    /** "scheduled" or "recording" when it's on the DVR's list (in the guide). */
    val recording: String? = null,
    /** A series covers it (in the guide). */
    val series: Boolean? = null,
) {
    val isSetToRecord: Boolean get() = recording != null
    val isRecordingNow: Boolean get() = recording == "recording"

    /** How far through it is at [now], 0 to 1. */
    fun progress(now: Instant = Instant.now()): Float {
        val length = end.toEpochMilli() - start.toEpochMilli()
        return if (length > 0) ((now.toEpochMilli() - start.toEpochMilli()).toFloat() / length).coerceIn(0f, 1f) else 0f
    }

    /** "Doctor Who: The Star Beast", or just the title when the episode's name repeats it or runs long. */
    val displayTitle: String
        get() = if (subtitle == null || subtitle == title || subtitle.length >= 60) title else "$title: $subtitle"
}

@Serializable
data class LiveChannel(
    val id: String,
    /** The channel number viewers know it by; the source may use decimals. */
    val number: Double,
    val name: String,
    val group: String? = null,
    /** A server path; logos are anonymous, like artwork. */
    val logoUrl: String? = null,
    /** Starred by this profile. */
    val isFavourite: Boolean = false,
    val now: LiveProgramme? = null,
    val next: LiveProgramme? = null,
) {
    /** "101", or "101.1" for a decimal one. */
    val numberText: String get() = if (number == Math.rint(number)) number.toLong().toString() else number.toString()

    /** The group's name without the provider's decoration: "UK| NEWS ᴴᴰ/ᴿᴬᵂ" reads "UK NEWS". */
    val groupName: String? get() = group?.let(::plainGroupName)
}

/** Superscript and small-capital letters, as providers dress up their group names, taken out; bars as spaces. */
internal fun plainGroupName(group: String): String {
    val kept = buildString {
        var i = 0
        while (i < group.length) {
            val code = group.codePointAt(i)
            val decoration = code in 0x1D00..0x1DBF || code in 0x2070..0x209F || code in 0x02B0..0x02FF || code in 0x2C7C..0x2C7D
            if (!decoration) appendCodePoint(code)
            i += Character.charCount(code)
        }
    }
    val text = kept.replace('|', ' ').trimEnd('/', ' ').split(' ').filter { it.isNotEmpty() }.joinToString(" ")
    return text.ifEmpty { group }
}

/** A channel being watched. [playlist] is a live HLS playlist whose address is the viewer's capability. */
@Serializable
data class LiveWatch(
    val viewer: String,
    val playlist: String,
    val channel: LiveChannel,
    /** How the channel reaches the player ("Original", "Converted to 720p, deinterlaced"). */
    val method: String,
    val rewindSeconds: Int = 0,
    /** As `PlaybackInfo.playbackId`, for logs; servers may leave it out. */
    val playbackId: String? = null,
)

/** A channel in the guide, with its programmes in the window asked for. */
@Serializable
data class GuideChannel(
    val id: String,
    val number: Double,
    val name: String,
    val group: String? = null,
    val logoUrl: String? = null,
    val isFavourite: Boolean = false,
    val programmes: List<LiveProgramme> = emptyList(),
)

@Serializable
data class LiveGuide(
    @Serializable(with = InstantSerializer::class) val from: Instant,
    @Serializable(with = InstantSerializer::class) val until: Instant,
    val channels: List<GuideChannel>,
)

// Recordings (the DVR)

/** A showing to record, being recorded, or recorded. */
@Serializable
data class Recording(
    val id: String,
    val ruleId: String? = null,
    val channelId: String,
    val channelName: String,
    val title: String,
    val subtitle: String? = null,
    val description: String? = null,
    val season: Int? = null,
    val episode: Int? = null,
    val isNew: Boolean = false,
    @Serializable(with = InstantSerializer::class) val start: Instant,
    @Serializable(with = InstantSerializer::class) val end: Instant,
    /** scheduled, recording, completed, failed, cancelled or skipped. */
    val state: String,
    /** Why it failed or was skipped, or a note ("Stopped early."). */
    val reason: String? = null,
    val size: Long? = null,
    /** Won't record as things stand: recordings scheduled before it want every stream. */
    val clashes: Boolean = false,
    /** In the library, to play. */
    val itemId: String? = null,
) {
    val isRecordingNow: Boolean get() = state == "recording"
}

/** A series being recorded: every showing of a title. */
@Serializable
data class RecordingSeries(
    val id: String,
    val title: String,
    /** Only on this channel; null for any. */
    val channelId: String? = null,
    val channelName: String? = null,
    val newOnly: Boolean = false,
    /** 0 keeps them all. */
    val keepLatest: Int = 0,
    val upcoming: Int = 0,
    val recorded: Int = 0,
) {
    fun covers(programme: LiveProgramme, channelId: String): Boolean =
        title.trim().equals(programme.title.trim(), ignoreCase = true) && (this.channelId == null || this.channelId == channelId)
}

@Serializable
data class Recordings(
    val upcoming: List<Recording> = emptyList(),
    val recent: List<Recording> = emptyList(),
    val series: List<RecordingSeries> = emptyList(),
    /** How many channels can be open at once, recordings and live viewing together. */
    val streams: Int = 0,
    val folder: String = "",
    val freeBytes: Long? = null,
) {
    /** The recording of this showing, if it's on the list. */
    fun recording(programme: LiveProgramme, channelId: String): Recording? =
        upcoming.firstOrNull { it.channelId == channelId && kotlin.math.abs(it.start.toEpochMilli() - programme.start.toEpochMilli()) < 1_000 }
}

/** Why a channel can't be watched: every stream is recording, this among them. */
data class RecordingInTheWay(val recordingId: String, val channelId: String, val channelName: String, val message: String) {
    companion object {
        /** Read from the server's 409 problem details; null if it isn't one of these. */
        fun parse(text: String): RecordingInTheWay? {
            val problem = runCatching { FlexarrClient.json.parseToJsonElement(text).jsonObject }.getOrNull() ?: return null
            fun field(name: String) = (problem[name] as? JsonPrimitive)?.takeIf { it.isString }?.content
            val recordingId = field("recordingId") ?: return null
            val channelId = field("channelId") ?: return null
            return RecordingInTheWay(recordingId, channelId, field("channelName") ?: "that channel", field("detail") ?: "Every stream is recording.")
        }
    }
}

@Serializable
internal data class RecordBody(val programmeId: Long)

@Serializable
internal data class RecordSeriesBody(val programmeId: Long, val anyChannel: Boolean, val newOnly: Boolean)

@Serializable
internal data class WatchBody(val replaces: String?, val audio: String?)

// Client

/** Servers before Live TV have none. */
suspend fun FlexarrClient.liveTvStatus(): LiveTvStatus? =
    try {
        decodeText(serializer(), sendRaw("GET", "livetv/status"))
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) null else throw e
    }

/** Every channel's programmes from [from] for [hours] (at most a day). */
suspend fun FlexarrClient.liveGuide(from: Instant, hours: Int): LiveGuide =
    decodeText(serializer(), send("GET", "livetv/guide", mapOf("from" to from.toString(), "hours" to hours.toString())))

/** Every channel in number order, with what's on now and next. */
suspend fun FlexarrClient.liveChannels(): List<LiveChannel> = decodeText(serializer(), sendRaw("GET", "livetv/channels"))

/**
 * Starts watching a channel and returns once it can be played. [replacing] is the viewer's previous channel, let go
 * first so its connection is free (providers often allow only one). [surround]: this device takes Dolby Digital, so a
 * channel's 5.1 comes as it is. Every stream recording throws [FlexarrException.RecordingInTheWayException].
 */
suspend fun FlexarrClient.watchLiveChannel(id: String, replacing: String? = null, surround: Boolean = false): LiveWatch =
    decodeText(
        serializer(),
        send("POST", "livetv/channels/$id/watch", body = FlexarrClient.json.encodeToString(WatchBody(replacing, if (surround) "surround" else null))),
    )

suspend fun FlexarrClient.recordings(): Recordings = decodeText(serializer(), sendRaw("GET", "livetv/recordings"))

suspend fun FlexarrClient.record(programmeId: Long): Recording =
    decodeText(serializer(), send("POST", "livetv/recordings", body = FlexarrClient.json.encodeToString(RecordBody(programmeId))))

suspend fun FlexarrClient.recordSeries(programmeId: Long, anyChannel: Boolean = false, newOnly: Boolean = false): RecordingSeries =
    decodeText(
        serializer(),
        send("POST", "livetv/recordings/series", body = FlexarrClient.json.encodeToString(RecordSeriesBody(programmeId, anyChannel, newOnly))),
    )

/** A scheduled one comes off the list; one recording now stops (keeping what it has); a finished one is deleted. */
suspend fun FlexarrClient.removeRecording(id: String) {
    send("DELETE", "livetv/recordings/$id")
}

/** The series stops: its scheduled showings come off the list; what it recorded stays. */
suspend fun FlexarrClient.removeSeries(id: String) {
    send("DELETE", "livetv/recordings/series/$id")
}

/** The player is still open, though paused (so not fetching anything). */
suspend fun FlexarrClient.liveHeartbeat(viewer: String) {
    send("POST", "livetv/watch/$viewer/heartbeat")
}

suspend fun FlexarrClient.stopLiveChannel(viewer: String) {
    send("DELETE", "livetv/watch/$viewer/")
}

suspend fun FlexarrClient.setFavouriteChannel(id: String, favourite: Boolean) {
    send(if (favourite) "PUT" else "DELETE", "livetv/channels/$id/favourite")
}
