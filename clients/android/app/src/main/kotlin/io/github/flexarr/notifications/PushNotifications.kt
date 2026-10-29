package io.github.flexarr.notifications

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.google.firebase.FirebaseApp
import com.google.firebase.messaging.FirebaseMessaging
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.NotificationDevice
import io.github.flexarr.core.NotificationSettings
import io.github.flexarr.core.PushPayload
import io.github.flexarr.core.notificationDevice
import io.github.flexarr.core.registerForNotifications
import io.github.flexarr.core.sendTestNotification
import io.github.flexarr.core.stopNotifications
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.data.ServerCredentials
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.tasks.await
import kotlinx.coroutines.withTimeoutOrNull
import java.util.Base64
import java.util.TimeZone

private val log = FlexarrLog("notifications")

/**
 * Notifications from the server (docs/notifications.md): what's new, sealed with a key only this phone and its server
 * know, delivered by Firebase and opened by the app ([FlexarrMessagingService]). The phone hears about one profile,
 * whichever it was on when they were turned on, so it registers as that profile (with that profile's token) whoever is
 * watching now. As the iPhone's PushNotifications.
 */
class PushNotifications(private val context: Context) {
    private val prefs = context.getSharedPreferences("notifications", Context.MODE_PRIVATE)
    private val store = CredentialStore(context)

    /** This build has a Firebase project to get notifications through (google-services.json was there to build with). */
    val isAvailable: Boolean get() = FirebaseApp.getApps(context).isNotEmpty()

    /** The main switch, as last set here. */
    var isOn by mutableStateOf(prefs.getBoolean(ON, false))
        private set

    /** Who they're for, as the server last said. */
    var profileId by mutableStateOf(prefs.getString(PROFILE, null))
        private set
    var profileName by mutableStateOf(prefs.getString(PROFILE_NAME, null))
        private set

    /** What this phone hears about, kept here so a new token can be sent with them. */
    var settings by mutableStateOf(
        prefs.getString(SETTINGS, null)?.let { runCatching { FlexarrClient.json.decodeFromString(NotificationSettings.serializer(), it) }.getOrNull() }
            ?: NotificationSettings(),
    )
        private set

    /** They've been turned on here and not turned off since; signing out leaves it set, so the next sign-in turns them back on. */
    private val wanted: Boolean get() = prefs.getBoolean(WANTED, false)

    /** The token of the profile they're for ("" for none, which the server takes as the main profile). */
    private val profileToken: String? get() = prefs.getString(PROFILE_TOKEN, null)

    /** Android lets the app post notifications (asked for on Android 13 on; Settings › Apps can turn them off). */
    val isAllowed: Boolean
        get() = NotificationManagerCompat.from(context).areNotificationsEnabled() &&
            (Build.VERSION.SDK_INT < 33 || ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED)

    // Turning on and off

    /** Registers this phone for the profile [credentials] act as (the permission is asked for by the screen first). */
    suspend fun turnOn(credentials: ServerCredentials): NotificationDevice =
        register(credentials, settings, useCurrentProfile = true)

    /** Stops them on the server (best effort) and forgets them here. */
    suspend fun turnOff(credentials: ServerCredentials?) {
        credentials?.let { runCatching { it.client().stopNotifications() } }
        forget()
        prefs.edit().putBoolean(WANTED, false).apply()
    }

    /**
     * A sign-in with them off, though they were on before it (this phone signed out and back in, say): on again for the
     * profile it's on, if Android still allows them. Quietly; Settings says so if it didn't work.
     */
    suspend fun resume(credentials: ServerCredentials) {
        if (isOn || !wanted || !isAvailable || !isAllowed) return
        runCatching { register(credentials, settings, useCurrentProfile = true) }
            .onFailure { log.warning("Couldn't turn notifications back on", it) }
    }

    /** Notifications for another profile from now on (the one [credentials] act as). */
    suspend fun move(credentials: ServerCredentials): NotificationDevice = register(credentials, settings, useCurrentProfile = true)

    /** Changes what this phone hears about, for the same profile. Shown at once; put back if the server refuses. */
    suspend fun update(changed: NotificationSettings, credentials: ServerCredentials): NotificationDevice {
        val before = settings
        saveSettings(changed)
        return try {
            register(forProfile(credentials), changed)
        } catch (e: Exception) {
            saveSettings(before)
            throw e
        }
    }

    /**
     * How the server has this phone, with the switch here having the last word: registered again if the server forgot
     * it (Google said the token went stale, say), stopped if they were turned off here while out of reach.
     */
    suspend fun load(credentials: ServerCredentials): NotificationDevice? {
        val device = credentials.client().notificationDevice()
        return when {
            device != null && isOn -> device.also(::remember)
            device == null && isOn -> register(forProfile(credentials), settings)
            device != null -> null.also { runCatching { credentials.client().stopNotifications() } }
            else -> null
        }
    }

    suspend fun sendTest(credentials: ServerCredentials) = credentials.client().sendTestNotification()

    /** Signing out: the server stops them (best effort, before the sign-in itself goes). */
    suspend fun signedOut(client: FlexarrClient) {
        if (isOn) runCatching { client.stopNotifications() }
        forget()
    }

    /** Forgets them here, key and all. [wanted] stays, for the next sign-in. */
    fun forget() {
        isOn = false
        profileId = null
        profileName = null
        prefs.edit().putBoolean(ON, false).remove(PROFILE).remove(PROFILE_NAME).remove(PROFILE_TOKEN).remove(KEY).remove(SENT_TOKEN).apply()
        if (isAvailable) FirebaseMessaging.getInstance().isAutoInitEnabled = false
    }

    // The Firebase token

    /** Firebase gave a new token: sent on, as the profile they're for, if the server doesn't have it yet. */
    suspend fun received(token: String) {
        if (!isOn || token == prefs.getString(SENT_TOKEN, null)) return
        val credentials = store.load() ?: return
        runCatching { register(forProfile(credentials), settings, token = token) }
            .onFailure { log.warning("Couldn't send the new Firebase token", it) }
    }

    /** No token from Firebase. */
    class NoToken : Exception("Couldn't reach Google's notification service. Check this phone is online and try again.")

    /**
     * Firebase stays off until notifications are turned on (the manifest says so), so the app doesn't reach Google
     * otherwise. From then on it keeps the token fresh by itself, until [forget].
     */
    private suspend fun token(): String {
        FirebaseMessaging.getInstance().isAutoInitEnabled = true
        return withTimeoutOrNull(15_000) { runCatching { FirebaseMessaging.getInstance().token.await() }.getOrNull() } ?: throw NoToken()
    }

    // Registering

    private suspend fun register(
        credentials: ServerCredentials,
        settings: NotificationSettings,
        useCurrentProfile: Boolean = false,
        token: String? = null,
    ): NotificationDevice {
        val firebaseToken = token ?: token()
        val device = credentials.client().registerForNotifications(firebaseToken, key(), TimeZone.getDefault().id, settings, useCurrentProfile)
        prefs.edit()
            .putString(SENT_TOKEN, firebaseToken)
            .putString(PROFILE_TOKEN, credentials.profileToken.orEmpty())
            .putBoolean(ON, true)
            .putBoolean(WANTED, true)
            .apply()
        isOn = true
        remember(device)
        return device
    }

    private fun remember(device: NotificationDevice) {
        profileId = device.profileId
        profileName = device.profileName
        prefs.edit().putString(PROFILE, device.profileId).putString(PROFILE_NAME, device.profileName).apply()
        saveSettings(device.settings)
    }

    private fun saveSettings(changed: NotificationSettings) {
        settings = changed
        prefs.edit().putString(SETTINGS, FlexarrClient.json.encodeToString(NotificationSettings.serializer(), changed)).apply()
    }

    /** The sign-in, as the profile notifications are for (rather than whoever is watching now). */
    private fun forProfile(credentials: ServerCredentials): ServerCredentials {
        val token = profileToken ?: return credentials
        return credentials.copy(profileToken = token.ifEmpty { null })
    }

    /** The key notifications are sealed with: made once, kept encrypted by the Keystore. Null if there's none yet. */
    fun savedKey(): ByteArray? = prefs.getString(KEY, null)?.let { runCatching { Base64.getDecoder().decode(store.decrypt(it)) }.getOrNull() }

    private fun key(): ByteArray = savedKey() ?: PushPayload.newKey().also {
        prefs.edit().putString(KEY, store.encrypt(Base64.getEncoder().encodeToString(it))).apply()
    }

    private companion object {
        const val ON = "on"
        const val WANTED = "wanted"
        const val PROFILE = "profile"
        const val PROFILE_NAME = "profileName"
        const val PROFILE_TOKEN = "profileToken"
        const val SETTINGS = "settings"
        const val KEY = "key"
        const val SENT_TOKEN = "sentToken"
    }
}
