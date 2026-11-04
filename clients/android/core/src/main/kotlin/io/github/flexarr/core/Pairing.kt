package io.github.flexarr.core

import io.github.flexarr.core.i18n.t
import kotlinx.serialization.Serializable
import kotlinx.serialization.serializer
import java.time.Instant

// Letting a new device in from a phone: a TV, phone or tablet showing a code asks to connect, and a phone signed in as
// the main profile is asked to allow it (rather than someone typing the code into the web app on a computer).

/** A device showing a code and waiting for someone to let it in. */
@Serializable
data class WaitingDevice(
    /** What it shows on its screen ("ABC-DEF"), to check against before allowing it. */
    val code: String,
    /** "Living room", as it named itself. */
    val deviceName: String,
    /** When the code runs out; the device shows a new one then. */
    @Serializable(with = InstantSerializer::class) val expiresAt: Instant,
    /** Its address, as the server saw it; null when not known. */
    val requestedFrom: String? = null,
    /** "Local" (the home network), "Tailscale", or something else (somewhere else). */
    val zone: String? = null,
    /** "AppleTv", "IPhone", "AndroidTv"…, as [Device.deviceType]. Absent from servers before 0.54; unknown ones are let be. */
    val deviceType: String? = null,
    /**
     * The device asking again by itself just after "Don't allow": listed, so it can still be allowed, but not asked
     * about. Absent from servers before 0.56.
     */
    val turnedAway: Boolean = false,
) {
    /** Whether to ask about it (the prompt) rather than only list it in Settings. */
    val asks: Boolean get() = !turnedAway

    /** "Apple TV", "Android phone"…; null when the server didn't say, or said something this app doesn't know. */
    val kind: String? get() = deviceKind(deviceType)

    /** "asking from your home network (192.168.1.70)", "asking over Tailscale"… */
    private val asking: String
        get() {
            val address = requestedFrom?.takeIf { it.isNotBlank() }
            return when (zone) {
                "Local" -> if (address == null) t("asking from your home network") else t("asking from your home network ({address})", "address" to address)
                "Tailscale" -> if (address == null) t("asking over Tailscale") else t("asking over Tailscale ({address})", "address" to address)
                else -> if (address == null) t("asking from outside your home network") else t("asking from {address}, outside your home network", "address" to address)
            }
        }

    /** Where it's asking from, in a sentence: "It's asking from your home network (192.168.1.70)." */
    val whereFrom: String get() = t("It’s {asking}.", "asking" to asking)

    /** What it is and where it's asking from: "Apple TV · asking from your home network (192.168.1.70)", else [whereFrom]. */
    val summary: String get() = kind?.let { t("{kind}, {asking}", "kind" to it, "asking" to asking) } ?: whereFrom

    /** Short, for a list: "Apple TV · home network", "Tailscale", "outside your home network". */
    val shortSummary: String
        get() {
            val network = when (zone) {
                "Local" -> t("home network")
                "Tailscale" -> "Tailscale"
                else -> t("outside your home network")
            }
            return listOfNotNull(kind, network).joinToString(", ")
        }
}

/** [device] in [this] list: in place of the one with its code (a code still on screen lasts longer), else at the end. */
fun List<WaitingDevice>.updating(device: WaitingDevice): List<WaitingDevice> {
    val index = indexOfFirst { it.code == device.code }
    return if (index < 0) this + device else toMutableList().also { it[index] = device }
}

@Serializable
internal data class PairingEnded(val code: String)

/** What the command stream carries: commands for this device and, for the main profile, devices asking to connect. */
sealed interface DeviceEvent {
    data class Command(val command: DeviceCommand) : DeviceEvent
    /** A device started waiting (or was already waiting when the stream opened). */
    data class Pairing(val device: WaitingDevice) : DeviceEvent
    /** The device with [code] was let in (from anywhere), turned away, or its code ran out. */
    data class PairingEnded(val code: String) : DeviceEvent
}

/** Codes are letters, digits and a dash: anything else could reach another of the server's paths once put in one. */
internal fun isPairingCode(code: String): Boolean = code.length in 1..32 && code.all { it.isLetterOrDigit() || it == '-' }

/** The devices waiting to be let in now. Only the main profile may ask; empty from servers before it could be asked. */
suspend fun FlexarrClient.waitingDevices(): List<WaitingDevice> =
    try {
        get("pairing/waiting")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) emptyList() else throw e
    }

/**
 * Lets the device showing [code] in. A code no longer waiting (it ran out, and the device shows a new one) throws
 * [FlexarrException.Http] 404; too many let in within the hour, 429 with the server's reason as its message.
 */
suspend fun FlexarrClient.allowDevice(code: String) {
    if (!isPairingCode(code)) throw FlexarrException.Http(404, null)
    send("POST", "pairing/code/$code/approve", body = "{}")
}

/** Turns the device showing [code] away. */
suspend fun FlexarrClient.declineDevice(code: String) {
    if (!isPairingCode(code)) throw FlexarrException.Http(404, null)
    send("POST", "pairing/code/$code/decline", body = "{}")
}

/** Reads one of the stream's events by name; null for names this app doesn't know (newer servers' events) or a body it can't read. */
internal fun deviceEvent(event: ServerSentEvents.Event): DeviceEvent? {
    val json = FlexarrClient.json
    return when (event.name) {
        "command" -> DeviceEvent.Command(json.decodeFromString(serializer<DeviceCommand>(), event.data))
        // A prompt that can't be read is let go rather than closing the stream that commands come on.
        "pairing" -> runCatching { json.decodeFromString(serializer<WaitingDevice>(), event.data) }.getOrNull()?.let { DeviceEvent.Pairing(it) }
        "pairing-ended" -> runCatching { json.decodeFromString(serializer<PairingEnded>(), event.data) }.getOrNull()?.let { DeviceEvent.PairingEnded(it.code) }
        else -> null
    }
}
