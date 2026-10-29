package io.github.flexarr.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.filterIsInstance
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOn
import kotlinx.serialization.Serializable
import kotlinx.serialization.serializer
import okhttp3.Request
import java.io.IOException
import java.time.Instant
import java.util.concurrent.TimeUnit

// Playing on another screen, and being one: the server's devices and the command stream, as FlexarrKit's devices
// endpoints and ServerSentEvents.swift.

/** What a device is playing, as the server last heard. */
@Serializable
data class NowPlaying(val item: ItemSummary, val positionSeconds: Double, val durationSeconds: Double, val paused: Boolean)

/** A TV, phone or tablet signed in to the server, as a place to play something. */
@Serializable
data class Device(
    val id: String,
    val name: String,
    /** Flexarr is open on it and would take a command now. Offline, a command waits two minutes for it. */
    val online: Boolean,
    @Serializable(with = InstantSerializer::class) val lastUsedAt: Instant,
    val profileId: String,
    val profileName: String? = null,
    /** The device asking. */
    val isThisOne: Boolean = false,
    val nowPlaying: NowPlaying? = null,
    /** "AppleTv", "IPhone", "AndroidTv"…, to name and draw it. Absent from servers before 0.54. */
    val deviceType: String? = null,
)

@Serializable(with = DeviceCommandKind.Serializer::class)
enum class DeviceCommandKind(override val raw: String) : ForwardCompatibleEnum {
    Play("Play"), Pause("Pause"), Resume("Resume"), Stop("Stop"), Watch("Watch"), Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<DeviceCommandKind>("DeviceCommandKind", entries, Unknown)
}

/** Something another device, the web app or an assistant asked this device to do. */
@Serializable
data class DeviceCommand(
    val id: String,
    val kind: DeviceCommandKind,
    /** With [DeviceCommandKind.Play], the film, episode or track to start (never a show: the server has worked out the episode). */
    val itemId: String? = null,
    val fromBeginning: Boolean = false,
    /** Who asked ("Dan's iPhone"), when known. */
    val sentBy: String? = null,
    /** With play and a show as the item: its episodes in a random order. */
    val shuffle: Boolean = false,
    /** With [DeviceCommandKind.Watch], the Live TV channel to put on. */
    val channelId: String? = null,
    /**
     * With [DeviceCommandKind.Play], the profile to play it as: switch to it first. Absent from servers before 0.54,
     * and when it's whoever is watching here.
     */
    val profileId: String? = null,
)

/** What came of telling a device to do something. */
@Serializable
data class DeviceCommandResult(
    /** The device took it. False: Flexarr isn't open on it; the command waits two minutes for it to be opened. */
    val delivered: Boolean,
    val deviceName: String,
    /** For play: what will play (the episode, when a show was asked for). */
    val item: ItemSummary? = null,
    /** For watch: the channel being put on. */
    val channel: String? = null,
)

/** Pause, resume or stop, for [control]. */
enum class DeviceControl(val raw: String) { Pause("pause"), Resume("resume"), Stop("stop") }

@Serializable
internal data class DevicePlayBody(
    val itemId: String,
    val fromBeginning: Boolean,
    val shuffle: Boolean,
    /** Left out when null (explicitNulls is off): servers before 0.54 don't know it. */
    val profileId: String? = null,
)

@Serializable
internal data class DeviceWatchBody(val channelId: String)

/**
 * Reads a server-sent events stream line by line: `event:` names the event, `data:` lines (joined with newlines) are
 * its body, a blank line ends it, and lines starting with `:` are comments (keep-alives).
 */
class ServerSentEvents {
    data class Event(val name: String, val data: String)

    private var name = "message"
    private val data = mutableListOf<String>()

    /** Feeds one line (without its line break); returns the event it completes, if any. */
    fun feed(line: String): Event? {
        if (line.isEmpty()) {
            if (data.isEmpty()) {
                name = "message"
                return null
            }
            val event = Event(name, data.joinToString("\n"))
            name = "message"
            data.clear()
            return event
        }
        if (line.startsWith(":")) return null
        val colon = line.indexOf(':')
        val field = if (colon < 0) line else line.substring(0, colon)
        val value = if (colon < 0) "" else line.substring(colon + 1).removePrefix(" ")
        when (field) {
            "event" -> name = value
            "data" -> data += value
            // id and retry aren't used here.
        }
        return null
    }
}

/** Every TV, phone and tablet signed in to the server, the ones with Flexarr open first. */
suspend fun FlexarrClient.devices(): List<Device> = decodeText(serializer(), sendRaw("GET", "devices"))

/**
 * Tells a device to play a title. A show's or season's id plays the episode that device's profile (or [profileId], to
 * play it as someone else there) is up to; the result says which, and whether the device took it (Flexarr must be open
 * on it). Servers before 0.54 ignore [profileId].
 */
suspend fun FlexarrClient.playOn(
    deviceId: String,
    itemId: String,
    fromBeginning: Boolean = false,
    shuffle: Boolean = false,
    profileId: String? = null,
): DeviceCommandResult =
    decodeText(
        serializer(),
        send("POST", "devices/$deviceId/play", body = FlexarrClient.json.encodeToString(DevicePlayBody(itemId, fromBeginning, shuffle, profileId))),
    )

/** Puts a Live TV channel on another device. */
suspend fun FlexarrClient.watchOn(deviceId: String, channelId: String): DeviceCommandResult =
    decodeText(serializer(), send("POST", "devices/$deviceId/watch", body = FlexarrClient.json.encodeToString(DeviceWatchBody(channelId))))

/** Pauses, resumes or stops what a device is playing. */
suspend fun FlexarrClient.control(deviceId: String, action: DeviceControl): DeviceCommandResult =
    decodeText(serializer(), send("POST", "devices/$deviceId/${action.raw}"))

/** The server writes a comment every 20 seconds, so a quiet minute and a half means the connection has gone. */
private val streamHttp by lazy { FlexarrClient.sharedHttp.newBuilder().readTimeout(90, TimeUnit.SECONDS).build() }

/** Commands sent to this device, as they arrive: [deviceEvents] without the rest. */
fun FlexarrClient.commandStream(): Flow<DeviceCommand> = deviceEvents().filterIsInstance<DeviceEvent.Command>().map { it.command }

/**
 * What the server tells this device, as it arrives, over one long-lived request (server-sent events): commands, and
 * for the main profile, devices asking to connect. Ends when the server closes it (a newer connection from this device
 * took over) or the connection drops; the caller opens it again. Only a device signed in with its own token has one:
 * with the API key the server answers 403.
 */
fun FlexarrClient.deviceEvents(): Flow<DeviceEvent> = flow {
    val request = Request.Builder()
        .url(baseUrl.newBuilder().addPathSegments("api/v1/devices/commands").build())
        .header("Accept", "text/event-stream")
        .signIn()
        .build()
    val call = streamHttp.newCall(request)
    coroutineScope {
        // The connection and its reads block: cancelling the collector (the app going to the background) cancels the
        // call from beside them, so the connection doesn't live on unseen.
        val cancelsCall = launch { try { awaitCancellation() } finally { call.cancel() } }
        try {
            val response = try {
                call.execute()
            } catch (e: IOException) {
                ensureActive()
                throw FlexarrException.Unreachable(e)
            }
            response.use {
                when {
                    it.code == 401 -> {
                        onUnauthorized?.invoke()
                        throw FlexarrException.Unauthorized()
                    }
                    !it.isSuccessful -> throw FlexarrException.Http(it.code, null)
                }
                val source = it.body.source()
                val events = ServerSentEvents()
                try {
                    while (true) {
                        // The server's keep-alive comments every 20 seconds emit nothing, so it's checked here too.
                        ensureActive()
                        val line = source.readUtf8Line() ?: break
                        val event = events.feed(line) ?: continue
                        deviceEvent(event)?.let { emit(it) }
                    }
                } catch (e: IOException) {
                    ensureActive()
                    throw FlexarrException.Unreachable(e)
                }
            }
        } finally {
            cancelsCall.cancel()
        }
    }
}.flowOn(Dispatchers.IO)
