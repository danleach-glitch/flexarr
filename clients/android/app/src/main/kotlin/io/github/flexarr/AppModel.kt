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
import io.github.flexarr.audio.MusicPlayer
import io.github.flexarr.audio.PodcastPlayer
import io.github.flexarr.core.PodcastEpisode
import io.github.flexarr.core.PodcastPreferences
import io.github.flexarr.core.PodcastStatus
import io.github.flexarr.core.Profile
import io.github.flexarr.core.addToUpNext
import io.github.flexarr.core.podcastPreferences
import io.github.flexarr.core.podcastStatus
import io.github.flexarr.core.removeFromUpNext
import io.github.flexarr.core.upNext
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
     * Debug builds started with `--ez noHistory true` play without reporting progress or remembering choices made while
     * playing (subtitles, a show's speed, subtitle size and position), so trying things against the household's server
     * doesn't change anyone's watch state or settings.
     */
    var recordsHistory = true

    /** Goes up whenever something may have changed what's watched (the player closed, say): screens load again. */
    var watchStateVersion by mutableIntStateOf(0)
        private set

    /** Goes up when the libraries' order changes, so the Libraries tab loads them again. */
    var librariesVersion by mutableIntStateOf(0)

    /** Podcast episodes, playing on under every screen. */
    val podcasts = PodcastPlayer(context, this)

    /** Music, playing on under every screen; it and podcasts take turns. */
    val music = MusicPlayer(context, this)

    /** Whether this profile has podcasts (null: not known, or a server from before them). */
    var podcastStatus: PodcastStatus? by mutableStateOf(null)
        private set

    /** How this profile listens to podcasts (its speed, above all), once loaded. */
    var podcastPreferences: PodcastPreferences? by mutableStateOf(null)

    /** The episodes to hear next, in order, the same on every device. */
    var upNext: List<PodcastEpisode> by mutableStateOf(emptyList())
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
        // The server's address for away from home comes with its status; it's kept beside this one.
        val status = credentials.client().systemStatus()
        credentials = credentials.copy(remoteAddress = remoteAddress(status.remoteUrl, credentials.address))
        credentials = withOwnToken(credentials)
        store.save(credentials)
        saved = true
        signedIn(credentials)
    }

    /** Signs in for this launch only, without saving or swapping the key: debug builds started from the command line. */
    fun signInForThisLaunch(address: String, apiKey: String, remote: String? = null) {
        val url = FlexarrClient.normalizeAddress(address) ?: return
        saved = false
        signedIn(ServerCredentials(url.toString(), apiKey, remoteAddress = remote?.let { FlexarrClient.normalizeAddress(it)?.toString() }))
    }

    private fun signedIn(credentials: ServerCredentials) {
        profiles = emptyList()
        profilesLoaded = false
        signedOutNotice = null
        phase = Phase.SignedIn(credentials)
    }

    /** Set once this launch has asked the server for its address for away from home. */
    private var remoteChecked = false

    /**
     * Keeps the saved address for away from home in step with the server's (remote access turned on or off since, or
     * a sign-in from before the app kept one). Best effort, once per launch, after the profiles are up.
     */
    suspend fun refreshRemoteAddress() {
        val credentials = credentials ?: return
        if (remoteChecked) return
        remoteChecked = true
        val status = runCatching { credentials.client().systemStatus() }.getOrNull() ?: return
        val remote = remoteAddress(status.remoteUrl, credentials.address)
        // Who's watching may have been chosen meanwhile: what matters is that it's still the same sign-in.
        val now = this.credentials ?: return
        if (remote == now.remoteAddress || now.address != credentials.address || now.apiKey != credentials.apiKey) return
        if (saved) store.saveRemoteAddress(remote)
        phase = Phase.SignedIn(now.copy(remoteAddress = remote))
    }

    /** The server's remote address as a base URL, or null when it has none or it's the address already in use. */
    private fun remoteAddress(remoteUrl: String?, address: String): String? =
        remoteUrl?.let { FlexarrClient.normalizeAddress(it)?.toString() }?.takeIf { it != address }

    fun signOut() {
        // Tell the server too, so this device's token stops working; best effort, it's forgotten here either way. Not
        // for a sign-in from the command line, which may be the server's own API key.
        if (saved) client?.let { client -> scope.launch { runCatching { client.signOut() } } }
        forget()
    }

    private fun forget() {
        music.stop()
        podcasts.stop()
        podcastStatus = null
        upNext = emptyList()
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
        // Someone else's music, podcasts and Up Next.
        music.stop()
        podcasts.stop()
        upNext = emptyList()
        if (saved) store.saveProfile(null)
        phase = Phase.SignedIn(credentials.copy(profileToken = null))
        profiles = profiles.map { it.copy(isCurrent = false) }
    }

    /**
     * Saves the order of the current profile's libraries in the menu (every entry, see [io.github.flexarr.core.libraryOrder])
     * and keeps the profile the server answers with. Throws if the server refuses.
     */
    suspend fun setLibraryOrder(order: List<String>) {
        val client = client ?: return
        val profile = currentProfile ?: return
        val saved = client.setLibraryOrder(profile.id, order)
        profiles = profiles.map { if (it.id == saved.id) saved.copy(isCurrent = it.isCurrent) else it }
        librariesVersion++
    }

    /** Whether this profile has podcasts and how it listens to them: asked once the profile is known. */
    suspend fun loadPodcastStatus() {
        val client = client ?: return
        podcastStatus = runCatching { client.podcastStatus() }.getOrNull()
        if (podcastStatus?.available == true) {
            runCatching { client.podcastPreferences() }.onSuccess { podcastPreferences = it }
        }
    }

    suspend fun loadUpNext() {
        val client = client ?: return
        if (podcastStatus?.available == false) return
        runCatching { client.upNext() }.onSuccess { upNext = it }
    }

    /** Puts an episode in Up Next: first (Play next), or last. */
    fun addToUpNext(episodeId: String, next: Boolean) {
        val client = client ?: return
        scope.launch { runCatching { client.addToUpNext(episodeId, next) }.onSuccess { upNext = it } }
    }

    fun removeFromUpNext(episodeId: String) {
        val client = client ?: return
        upNext = upNext.filterNot { it.id == episodeId }
        scope.launch { runCatching { client.removeFromUpNext(episodeId) } }
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
