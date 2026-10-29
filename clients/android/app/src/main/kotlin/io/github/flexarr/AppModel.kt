package io.github.flexarr

import android.content.Context
import android.os.Build
import android.provider.Settings
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.Profile
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.data.ServerCredentials
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Signed in or not, who's watching, and what every screen needs to reach the server. One for the app. */
class AppModel(private val context: Context) {
    sealed interface Phase {
        data object SignedOut : Phase
        data class SignedIn(val credentials: ServerCredentials) : Phase
    }

    val store = CredentialStore(context)

    /** Outlives any screen: signing out on the server and the last progress report finish after the player closes. */
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    var phase: Phase by mutableStateOf(store.load()?.let { Phase.SignedIn(it) } ?: Phase.SignedOut)
        private set

    /** Said on the connect screen after the server signed this device out. */
    var signedOutNotice: String? by mutableStateOf(null)
        private set

    var profiles: List<Profile> by mutableStateOf(emptyList())
        private set

    /** The profiles have been asked for since signing in (whether or not the server answered). */
    var profilesLoaded by mutableStateOf(false)
        private set

    /**
     * Debug builds started with `--ez noHistory true` play without reporting progress, so trying things against the
     * household's server doesn't change anyone's watch state.
     */
    var recordsHistory = true

    /** Goes up whenever something may have changed what's watched (the player closed, say): screens load again. */
    var watchStateVersion by mutableIntStateOf(0)
        private set

    /** The sign-in is saved on this device (not one for this launch only, from the command line). */
    private var saved = phase is Phase.SignedIn

    val credentials: ServerCredentials? get() = (phase as? Phase.SignedIn)?.credentials

    val client: FlexarrClient?
        get() = credentials?.let { credentials -> credentials.client(onUnauthorized = { checkSignIn(credentials) }) }

    val currentProfile: Profile? get() = profiles.firstOrNull { it.isCurrent }

    /** Someone has to choose who's watching: more than one profile, and none chosen on this device yet. */
    val needsProfile: Boolean get() = profilesLoaded && credentials?.profileToken == null && profiles.size > 1

    /** This device's name, as the server's list of signed-in devices shows it. */
    val deviceName: String
        get() = Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME)?.takeIf { it.isNotBlank() }
            ?: Build.MODEL

    /**
     * Checks the address and key against `/system/status`, then saves them. A typed API key is swapped for this
     * device's own token first, so the key itself isn't kept here.
     */
    suspend fun connect(address: String, apiKey: String) {
        val url = FlexarrClient.normalizeAddress(address) ?: throw FlexarrException.InvalidAddress()
        var credentials = ServerCredentials(url.toString(), apiKey.trim())
        credentials.client().systemStatus()
        credentials = withOwnToken(credentials)
        store.save(credentials)
        saved = true
        signedIn(credentials)
    }

    /** Signs in for this launch only, without saving or swapping the key: debug builds started from the command line. */
    fun signInForThisLaunch(address: String, apiKey: String) {
        val url = FlexarrClient.normalizeAddress(address) ?: return
        saved = false
        signedIn(ServerCredentials(url.toString(), apiKey))
    }

    private fun signedIn(credentials: ServerCredentials) {
        profiles = emptyList()
        profilesLoaded = false
        signedOutNotice = null
        phase = Phase.SignedIn(credentials)
    }

    fun signOut() {
        // Tell the server too, so this device's token stops working; best effort, it's forgotten here either way. Not
        // for a sign-in from the command line, which may be the server's own API key.
        if (saved) client?.let { client -> scope.launch { runCatching { client.signOut() } } }
        forget()
    }

    private fun forget() {
        if (saved) store.clear()
        profiles = emptyList()
        profilesLoaded = false
        phase = Phase.SignedOut
    }

    suspend fun loadProfiles() {
        val client = client ?: return
        try {
            val list = client.profiles()
            // A token for a profile that's since been removed acts as nobody: forget it and ask again.
            val credentials = credentials
            if (list.none { it.isCurrent } && credentials?.profileToken != null) {
                if (saved) store.saveProfile(null)
                phase = Phase.SignedIn(credentials.copy(profileToken = null))
            }
            profiles = list
        } catch (_: FlexarrException) {
            // Unreachable for now: the screens say so and try again.
        } finally {
            profilesLoaded = true
        }
    }

    /** Checks the PIN (if any) and makes [profile] the one watching on this device. A wrong PIN throws, saying so. */
    suspend fun chooseProfile(profile: Profile, pin: String? = null) {
        val credentials = credentials ?: return
        val selection = credentials.client().selectProfile(profile.id, pin)
        val updated = credentials.copy(profileToken = selection.token)
        // Saved only for a saved sign-in: one for this launch alone stays that way.
        if (saved) store.saveProfile(selection.token)
        phase = Phase.SignedIn(updated)
        profiles = profiles.map { it.copy(isCurrent = it.id == profile.id) }
        watchStateVersion++
    }

    /** Back to choosing who's watching. */
    fun switchProfile() {
        val credentials = credentials ?: return
        if (saved) store.saveProfile(null)
        phase = Phase.SignedIn(credentials.copy(profileToken = null))
        profiles = profiles.map { it.copy(isCurrent = false) }
    }

    fun watchStateChanged() {
        watchStateVersion++
    }

    private var checkingSignIn = false

    /**
     * A request was refused (401). If the server still refuses this sign-in when asked directly, it was signed out
     * there (from the web's Sign-ins list, or a new key): forget it and go back to connecting. One refusal alone could
     * be a server mid-restart.
     */
    private fun checkSignIn(refused: ServerCredentials) {
        scope.launch {
            if (checkingSignIn || credentials != refused) return@launch
            checkingSignIn = true
            try {
                delay(1_000)
                refused.client().me()
            } catch (_: FlexarrException.Unauthorized) {
                if (credentials == refused) {
                    forget()
                    signedOutNotice = "This device was signed out."
                }
            } catch (_: Exception) {
                // Unreachable or restarting: no reason to forget the sign-in.
            } finally {
                checkingSignIn = false
            }
        }
    }

    /**
     * The same sign-in with this device's own token in place of the server's API key, if it holds the key. Unchanged
     * if it doesn't, or the server is too old to offer one.
     */
    private suspend fun withOwnToken(credentials: ServerCredentials): ServerCredentials {
        if (credentials.apiKey.isEmpty()) return credentials
        return runCatching {
            val client = credentials.client()
            if (client.me().method != "api-key") return credentials
            credentials.copy(apiKey = client.deviceToken(deviceName))
        }.getOrDefault(credentials)
    }
}
