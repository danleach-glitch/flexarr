package io.github.flexarr.core

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

// Notifications from the server (docs/notifications.md): the phone registers with its Firebase token and a key of its
// own, and the server seals each notification with that key. As FlexarrKit's Notifications.swift.

/** What a phone is told about. Upcoming (each morning, what's airing that day) is off until asked for. */
@Serializable
data class NotificationSettings(
    val films: Boolean = true,
    val shows: Boolean = true,
    val podcasts: Boolean = true,
    val recordings: Boolean = true,
    /** When something this profile requested is ready. */
    val requests: Boolean = true,
    val upcoming: Boolean = false,
    /** The hour of the morning list, in the phone's time zone. */
    val upcomingHour: Int = 9,
)

/** This phone as the server has it registered for notifications (no key, no token). */
@Serializable
data class NotificationDevice(
    /** The one profile this phone hears about. */
    val profileId: String,
    val profileName: String? = null,
    val films: Boolean = true,
    val shows: Boolean = true,
    val podcasts: Boolean = true,
    val recordings: Boolean = true,
    val requests: Boolean = true,
    val upcoming: Boolean = false,
    val upcomingHour: Int = 9,
    val timeZone: String? = null,
    /** False when the server has notifications turned off: nothing will arrive, however it's set here. */
    val relay: Boolean = true,
) {
    val settings: NotificationSettings
        get() = NotificationSettings(films, shows, podcasts, recordings, requests, upcoming, upcomingHour)
}

/** What `PUT /notifications/device` takes, the settings beside the rest. */
@Serializable
private data class NotificationRegistration(
    val token: String,
    val environment: String,
    val key: String,
    val timeZone: String,
    val films: Boolean,
    val shows: Boolean,
    val podcasts: Boolean,
    val recordings: Boolean,
    val requests: Boolean,
    val upcoming: Boolean,
    val upcomingHour: Int,
    val useCurrentProfile: Boolean,
    /** Always sent: the encoder leaves default values out, and a missing platform means Apple. */
    val platform: String,
)

/** How this phone is registered for notifications; null when it isn't (or the server is from before them). */
suspend fun FlexarrClient.notificationDevice(): NotificationDevice? =
    try {
        get("notifications/device")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) null else throw e
    }

/**
 * Registers this phone (or updates it) with its Firebase [token] and notification [key] (32 bytes), for the profile
 * the client acts as. [useCurrentProfile]: notifications for the profile on now; otherwise a phone already registered
 * keeps its profile, whoever's watching.
 */
suspend fun FlexarrClient.registerForNotifications(
    token: String,
    key: ByteArray,
    timeZone: String,
    settings: NotificationSettings,
    useCurrentProfile: Boolean = false,
): NotificationDevice = sendDecoding(
    "PUT",
    "notifications/device",
    NotificationRegistration(
        token = token,
        environment = "production",
        key = Base64.getEncoder().encodeToString(key),
        timeZone = timeZone,
        films = settings.films,
        shows = settings.shows,
        podcasts = settings.podcasts,
        recordings = settings.recordings,
        requests = settings.requests,
        upcoming = settings.upcoming,
        upcomingHour = settings.upcomingHour,
        useCurrentProfile = useCurrentProfile,
        platform = "android",
    ),
)

/** Stops this phone's notifications. */
suspend fun FlexarrClient.stopNotifications() {
    send("DELETE", "notifications/device")
}

/** Sends a test notification now. The relay or Google refusing it comes back as the server's reason. */
suspend fun FlexarrClient.sendTestNotification() {
    send("POST", "notifications/device/test")
}

/** A notification as the server sealed it, opened by the app when Firebase delivers it. */
@Serializable
data class PushPayload(
    @SerialName("t") val title: String = "",
    @SerialName("b") val body: String = "",
    /** Decides the buttons (`flexarr.video`, `flexarr.podcast`, …). */
    @SerialName("c") val category: String? = null,
    /** Groups a show's notifications together. */
    @SerialName("g") val thread: String? = null,
    /** Artwork, a public URL. */
    @SerialName("i") val image: String? = null,
    /** What tapping opens and what Play plays (`item`, `play`, `podcast`, `episode`). */
    @SerialName("u") val info: Map<String, String> = emptyMap(),
) {
    companion object {
        const val VIDEO = "flexarr.video"
        const val PODCAST = "flexarr.podcast"
        const val RECORDING = "flexarr.recording"
        const val UPCOMING = "flexarr.upcoming"
        /** A device asking to connect (to the main profile's phones); `pairing` in [info] is the code it shows. */
        const val PAIRING = "flexarr.pairing"

        private const val NONCE = 12
        private const val TAG_BITS = 128

        /** Opens what the relay carried: base64 of nonce, ciphertext and tag (AES-256-GCM). Throws if it can't. */
        fun open(sealed: String, key: ByteArray): PushPayload {
            val data = Base64.getDecoder().decode(sealed)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_BITS, data, 0, NONCE))
            val plain = cipher.doFinal(data, NONCE, data.size - NONCE)
            return FlexarrClient.json.decodeFromString(serializer(), plain.decodeToString())
        }

        /** Seals [json] as the server does: for tests. */
        fun seal(json: String, key: ByteArray): String {
            val nonce = ByteArray(NONCE).also(SecureRandom()::nextBytes)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(TAG_BITS, nonce))
            return Base64.getEncoder().encodeToString(nonce + cipher.doFinal(json.toByteArray()))
        }

        /** A new 256-bit key for this phone's notifications. */
        fun newKey(): ByteArray = ByteArray(32).also(SecureRandom()::nextBytes)
    }
}
