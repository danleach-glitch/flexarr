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
import io.github.flexarr.core.LiveTvStatus
import io.github.flexarr.core.liveTvStatus
import io.github.flexarr.audio.MusicPlayer
import io.github.flexarr.audio.PodcastPlayer
import io.github.flexarr.core.PodcastEpisode
import io.github.flexarr.core.PodcastPreferences
import io.github.flexarr.core.PodcastStatus
import io.github.flexarr.core.Profile
import io.github.flexarr.core.ProfileSelection
import io.github.flexarr.core.SystemStatus
import io.github.flexarr.core.WatchlistAdded
import io.github.flexarr.core.WatchlistEntry
import io.github.flexarr.core.WatchlistStatus
import io.github.flexarr.core.addToWatchlist
import io.github.flexarr.core.removeFromWatchlist
import io.github.flexarr.core.removeLibraryItemFromWatchlist
import io.github.flexarr.core.requestTitle
import io.github.flexarr.core.watchlist
import io.github.flexarr.core.watchlistKeys
import io.github.flexarr.core.addToUpNext
import io.github.flexarr.core.podcastPreferences
import io.github.flexarr.core.podcastStatus
import io.github.flexarr.core.removeFromUpNext
import io.github.flexarr.core.upNext
import io.github.flexarr.data.CredentialStore
import io.github.flexarr.downloads.DownloadStore
import io.github.flexarr.logging.ErrorReports
import io.github.flexarr.logging.FlexarrLog
import io.github.flexarr.logging.LogHub
import io.github.flexarr.notifications.PushNotifications
import io.github.flexarr.widget.ContinueWatchingWidget
import io.github.flexarr.widget.PodcastWidgets
import io.github.flexarr.widget.WatchlistWidget
import io.github.flexarr.data.ServerCredentials
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ProcessLifecycleOwner
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import okhttp3.HttpUrl

private val flexarrLog = FlexarrLog("signin")

/**
 * Signed in or not, who's watching, and what every screen needs to reach the server. One for the app, starting from
 * the [savedSignIn] (read once at launch, as decrypting it takes a moment).
 */
class AppModel(private val context: Context, savedSignIn: ServerCredentials?) {
    sealed interface Phase {
        data object SignedOut : Phase
        data class SignedIn(val credentials: ServerCredentials) : Phase
    }

    val store = CredentialStore(context)

    /** Outlives any screen: signing out on the server and the last progress report finish after the player closes. */
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private var phaseState: Phase by mutableStateOf(savedSignIn?.let { Phase.SignedIn(it) } ?: Phase.SignedOut)

    var phase: Phase
        get() = phaseState
        private set(value) {
            phaseState = value
            signInChanged()
        }

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

    /** What other devices, the web app and assistants ask this one to play, while the app is in front. */
    val commands = DeviceCommands(context, this, scope)

    /** Listens for commands while the app is in front (any of its screens, the players included), and signed in. */
    fun listenForCommands() {
        val inFront = ProcessLifecycleOwner.get().lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)
        val client = client
        if (inFront && client != null) commands.start(client) else commands.stop()
        // Back in front: any that ended while the app was away go, and any waiting since are asked about.
        if (inFront && client != null && isMainProfile) scope.launch { pairing.refresh(client) }
    }

    /** Devices asking to connect, for the main profile to allow from here. */
    val pairing = PairingPrompts(context)

    /** This sign-in, as the profile watching, may manage the server now (the main profile, unlocked with its PIN). */
    var isAdmin by mutableStateOf(false)
        private set

    /** The main profile is watching, but managing the server takes its PIN first ([unlock]). */
    var canUnlock by mutableStateOf(false)
        private set

    /** The main profile has no PIN yet, so it can't be unlocked here until it's given one in a browser. */
    var adminPinMissing by mutableStateOf(false)
        private set

    /** The main profile is watching, unlocked or not: it's asked to let devices in. */
    val isMainProfile: Boolean get() = isAdmin || canUnlock

    /** This device is locked to kids profiles: the picker offers only them, until the admin PIN releases it. */
    var kidsLock by mutableStateOf(false)
        private set

    /** A child is watching: the kids app (no Settings). */
    val isKids: Boolean get() = currentProfile?.isKids == true

    /** This app has its own sign-in to the server (not its key, Tailscale or the local network), which a lock can stay on. */
    var hasOwnSignIn by mutableStateOf(false)
        private set

    /** A grown-up is watching on this app's own sign-in, the household has a kids profile and this device isn't locked yet. */
    val canLockToKids: Boolean get() = hasOwnSignIn && !isKids && !kidsLock && profiles.any { it.isKids }

    /** The picker's padlock over kids profiles: child lock on, or able to be turned on here, a kids profile included
     *  (Settings doesn't offer it there), where turning it on takes the admin PIN. */
    val showsKidsPadlock: Boolean get() = kidsLock || canLockToKids || (hasOwnSignIn && isKids)

    /** Asks whether this sign-in is the main profile (and may manage the server), and if so, which devices are waiting to connect. */
    suspend fun loadSignIn() {
        val client = client ?: return
        val me = runCatching { client.me() }.getOrNull() ?: return
        // Someone else may have been chosen meanwhile.
        if (client.profileToken != this.client?.profileToken) return
        isAdmin = me.isAdmin
        canUnlock = me.canUnlock
        adminPinMissing = me.adminPinMissing
        kidsLock = me.kidsLock
        hasOwnSignIn = me.method == "device"
        if (me.isMainProfile) pairing.refresh(client) else pairing.clear()
    }

    /** Unlocks managing the server with the admin PIN. Throws as [FlexarrClient.unlockAdmin] does. */
    suspend fun unlock(pin: String) {
        val client = client ?: return
        client.unlockAdmin(pin)
        if (client.profileToken != this.client?.profileToken) return
        isAdmin = true
        canUnlock = false
    }

    /** Managing the server turned out to be locked again (its 15 minutes ran out): asked afresh. */
    suspend fun locked() {
        isAdmin = false
        canUnlock = true
        loadSignIn()
    }

    private fun forgetSignIn() {
        isAdmin = false
        canUnlock = false
        adminPinMissing = false
        hasOwnSignIn = false
        pairing.clear()
    }

    /** Podcast episodes, playing on under every screen. */
    val podcasts = PodcastPlayer(context, this)

    /** Music, playing on under every screen; it and podcasts take turns. */
    val music = MusicPlayer(context, this)

    /** Goes up when Play or Shuffle starts music on the TV, which then shows Now Playing over everything. */
    var nowPlayingRequests by mutableIntStateOf(0)

    /** What's new, from the server through Firebase. */
    val notifications = PushNotifications(context)

    /** Films, episodes and podcast episodes kept on this phone to play with no connection. */
    val downloads = DownloadStore(context, this)

    /** The server couldn't be reached when the profiles were asked for: the app opens on Downloads, if there are any. */
    var serverUnreachable by mutableStateOf(false)
        private set

    /** Whether this profile has podcasts (null: not known, or a server from before them). */
    var podcastStatus: PodcastStatus? by mutableStateOf(null)
        private set

    /** How this profile listens to podcasts (its speed, above all), once loaded. */
    var podcastPreferences: PodcastPreferences? by mutableStateOf(null)

    /** Whether the server has Live TV, and how many channels (null: not known, or a server from before it). */
    var liveTvStatus: LiveTvStatus? by mutableStateOf(null)
        private set

    /** The live channel being started, for its card to show a spinner. */
    var liveStartingChannel: String? by mutableStateOf(null)

    val hasLiveTv: Boolean get() = liveTvStatus?.let { it.configured && it.channels > 0 } ?: false

    /** The server keeps watchlists (0.56 and later), so titles offer one. */
    var hasWatchlist by mutableStateOf(false)
        private set

    /** Library ids of the films and shows on this profile's watchlist, for cards' menus. */
    var watchlistItems: Set<String> by mutableStateOf(emptySet())
        private set

    /** "movie:123" / "tv:456" keys of every title on this profile's watchlist, for search beyond the library. */
    var watchlistTitles: Set<String> by mutableStateOf(emptySet())
        private set

    /** Goes up whenever the watchlist changes here, so the Watchlist screen loads it again. */
    var watchlistVersion by mutableIntStateOf(0)
        private set

    /**
     * Goes up only when a watchlist change could alter Home's "Ready from your watchlist" row (a library title went on
     * or off, or was requested), so taking off a title the library hasn't got doesn't load all of Home again.
     */
    var homeWatchlistVersion by mutableIntStateOf(0)
        private set

    /**
     * The profile's whole watchlist, for the Watchlist screen (it asks Seerr where each title has got to, so only there).
     * Null from a server without one; throws when the server can't be reached.
     */
    suspend fun loadWatchlist(): List<WatchlistEntry>? {
        val client = client ?: return null
        // A kids profile has no watchlist.
        if (isKids) return null.also { hasWatchlist = false }
        val list = client.watchlist()
        if (client.profileToken == this.client?.profileToken) hasWatchlist = list != null
        return list
    }

    /**
     * Whether the server keeps watchlists, and which library titles are on this profile's ([watchlistItems]), from the
     * cheap keys list. What's known stays when the server can't be reached.
     */
    suspend fun refreshWatchlist() {
        val client = client ?: return
        // A kids profile has no watchlist.
        if (isKids) return forgetWatchlist()
        try {
            val keys = client.watchlistKeys()
            // Someone else may have been chosen meanwhile.
            if (client.profileToken != this.client?.profileToken) return
            hasWatchlist = keys != null
            watchlistItems = keys?.libraryItemIds.orEmpty().toHashSet()
            watchlistTitles = keys?.titles.orEmpty().toHashSet()
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
        }
    }

    private var watchlistRefresh: Job? = null

    /**
     * [refreshWatchlist] and the watchlist widget, shortly: a run of changes (an offline sync sending a dozen
     * downloads' progress) asks the server once, after the last.
     */
    private fun refreshWatchlistSoon() {
        watchlistRefresh?.cancel()
        watchlistRefresh = scope.launch {
            delay(WATCHLIST_SETTLE_MS)
            refreshWatchlist()
            WatchlistWidget.refresh(context)
        }
    }

    /** Puts a library film or show on the watchlist, or takes it off. Throws if the server refuses. */
    suspend fun setOnWatchlist(itemId: String, on: Boolean): WatchlistAdded? {
        val client = client ?: return null
        val before = watchlistItems
        watchlistItems = if (on) before + itemId else before - itemId
        try {
            return if (on) {
                client.addToWatchlist(itemId)
            } else {
                client.removeLibraryItemFromWatchlist(itemId)
                null
            }
        } catch (e: Exception) {
            watchlistItems = before
            throw e
        } finally {
            watchlistChanged(affectsHome = true)
        }
    }

    /**
     * Puts a film or show from beyond the library on the watchlist, or takes it off, by "movie" or "tv" and its TMDB id
     * (search's results). Throws if the server refuses.
     */
    suspend fun setTitleOnWatchlist(mediaType: String, tmdbId: Int, on: Boolean): WatchlistAdded? {
        val client = client ?: return null
        val key = "$mediaType:$tmdbId"
        val before = watchlistTitles
        watchlistTitles = if (on) before + key else before - key
        try {
            return if (on) {
                client.addToWatchlist(mediaType, tmdbId)
            } else {
                client.removeFromWatchlist(mediaType, tmdbId)
                null
            }
        } catch (e: Exception) {
            watchlistTitles = before
            throw e
        } finally {
            watchlistChanged(affectsHome = false)
        }
    }

    /** Requests a film or show from beyond the library (search's results). Throws if the server (or Seerr) refuses. */
    suspend fun requestTitle(mediaType: String, tmdbId: Int) {
        val client = client ?: return
        try {
            client.requestTitle(mediaType, tmdbId)
        } finally {
            watchlistChanged(affectsHome = false)
        }
    }

    /** Takes an entry off the watchlist, from the Watchlist screen. Throws if the server refuses. */
    suspend fun removeFromWatchlist(entry: WatchlistEntry) {
        val client = client ?: return
        try {
            client.removeFromWatchlist(entry.id)
            entry.libraryItemId?.let { watchlistItems = watchlistItems - it }
        } finally {
            watchlistChanged(affectsHome = entry.inLibrary)
        }
    }

    /** Requests a title on the watchlist that nobody has asked for yet. Throws if the server (or Seerr) refuses. */
    suspend fun request(entry: WatchlistEntry) {
        val client = client ?: return
        val tmdbId = entry.tmdbId ?: return
        try {
            client.requestTitle(entry.mediaType, tmdbId)
        } finally {
            watchlistChanged(affectsHome = entry.inLibrary)
        }
    }

    /**
     * The watchlist changed here: its screen loads again, Home too when [affectsHome] (its ready row could change), and
     * the widget asks again.
     */
    private fun watchlistChanged(affectsHome: Boolean) {
        watchlistVersion++
        if (affectsHome) homeWatchlistVersion++
        scope.launch { WatchlistWidget.refresh(context) }
    }

    /** In the library (or the entry is ready there), so its going could change Home's ready row. */
    private val WatchlistEntry.inLibrary: Boolean
        get() = libraryItemId != null || item != null || status == WatchlistStatus.Ready

    private fun forgetWatchlist() {
        watchlistRefresh?.cancel()
        hasWatchlist = false
        watchlistItems = emptySet()
        watchlistTitles = emptySet()
    }

    /** The episodes to hear next, in order, the same on every device. */
    var upNext: List<PodcastEpisode> by mutableStateOf(emptyList())
        private set

    /** Where a notification, shortcut or the widget asked to go, until the screens have gone there. */
    var pendingLink: AppLink? by mutableStateOf(null)

    /** Words the Assistant asked to search for ("search Flexarr for Fargo"), until Search has typed them in. */
    var pendingSearch: String? by mutableStateOf(null)

    /** The podcast Now Playing sheet is open: from the bar, or after Play in an episode's notes. */
    var showsNowPlaying by mutableStateOf(false)

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
        // A host on the internet is tried over https first, then as typed would have been (see addressCandidates).
        val candidates = FlexarrClient.addressCandidates(address).ifEmpty { throw FlexarrException.InvalidAddress() }
        var credentials: ServerCredentials? = null
        var status: SystemStatus? = null
        for ((index, url) in candidates.withIndex()) {
            val trying = ServerCredentials(url.toString(), apiKey.trim())
            try {
                status = trying.client().systemStatus()
                credentials = trying
                break
            } catch (e: FlexarrException) {
                // Not there (or not a Flexarr server there): the next way of reaching it, if there's one left.
                val tryNext = e is FlexarrException.Unreachable || e is FlexarrException.UnexpectedResponse
                if (!tryNext || index == candidates.lastIndex) throw e
            }
        }
        // The server's address for away from home comes with its status; it's kept beside this one.
        credentials = credentials!!.copy(remoteAddress = remoteAddress(status!!, credentials.address))
        credentials = withOwnToken(credentials)
        store.save(credentials)
        saved = true
        signedIn(credentials)
        // On again, if they were on before this phone signed out.
        scope.launch { notifications.resume(credentials) }
    }

    /** Signs in for this launch only, without saving or swapping the key: debug builds started from the command line. */
    fun signInForThisLaunch(address: String, apiKey: String, remote: String? = null) {
        val url = FlexarrClient.normalizeAddress(address) ?: return
        saved = false
        signedIn(ServerCredentials(url.toString(), apiKey, remoteAddress = remote?.let { FlexarrClient.normalizeAddress(it)?.toString() }))
    }

    private fun signedIn(credentials: ServerCredentials) {
        forgetSignIn()
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
        // Once a launch too: the server says when the morning's "on today" arrives in this phone's time zone.
        scope.launch { notifications.checkTimeZone() }
        val status = runCatching { credentials.client().systemStatus() }.getOrNull() ?: return
        val remote = remoteAddress(status, credentials.address, kept = credentials.remoteAddress)
        // Who's watching may have been chosen meanwhile: what matters is that it's still the same sign-in.
        val now = this.credentials ?: return
        if (remote == now.remoteAddress || now.address != credentials.address || now.apiKey != credentials.apiKey) return
        if (saved) store.saveRemoteAddress(remote)
        phase = Phase.SignedIn(now.copy(remoteAddress = remote))
    }

    fun signOut() {
        // Tell the server too, so this device's token stops working; best effort, it's forgotten here either way. Not
        // for a sign-in from the command line, which may be the server's own API key.
        val client = client
        // Downloads go too: what was watched offline is sent and the server's copies dropped first, while the sign-in works.
        val downloadsDone = downloads.signedOut(client)
        if (saved) client?.let { client -> scope.launch { downloadsDone.join(); notifications.signedOut(client); runCatching { client.signOut() } } }
        forget()
    }

    private fun forget() {
        commands.stop()
        forgetSignIn()
        music.stop()
        podcasts.stop()
        podcastStatus = null
        upNext = emptyList()
        forgetWatchlist()
        if (saved) store.clear()
        profiles = emptyList()
        profilesLoaded = false
        phase = Phase.SignedOut
        scope.launch {
            ContinueWatchingWidget.refresh(context)
            PodcastWidgets.refresh(context)
            WatchlistWidget.forget(context)
        }
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
            // Locked to kids profiles, the first Who's watching offers Unlock, before anyone is chosen.
            runCatching { client.me() }.getOrNull()?.let {
                kidsLock = it.kidsLock
                hasOwnSignIn = it.method == "device"
            }
            serverUnreachable = false
        } catch (e: FlexarrException) {
            // Unreachable for now: the screens say so and try again (opening on Downloads, once the list's been read).
            if (e is FlexarrException.Unreachable) downloads.ready()
            serverUnreachable = e is FlexarrException.Unreachable
        } finally {
            profilesLoaded = true
        }
    }

    /**
     * Checks the PIN (if any) and makes [profile] the one watching on this device. A wrong PIN throws, saying so.
     * Leaving a kids profile takes the admin PIN as [parentPin]; without it, [FlexarrException.ParentPinNeeded].
     */
    suspend fun chooseProfile(profile: Profile, pin: String? = null, parentPin: String? = null) {
        val credentials = credentials ?: return
        val held = heldParentPin?.takeIf { System.currentTimeMillis() < heldParentPinUntil }
        chose(credentials.client().selectProfile(profile.id, pin, parentPin ?: held), profiles)
    }

    /**
     * The admin PIN, just given to turn child lock off: the next profile chosen within a minute takes it, so
     * leaving the kids profile doesn't ask for it again.
     */
    private var heldParentPin: String? = null
    private var heldParentPinUntil = 0L

    /** The admin PIN given here in the last minute (turning child lock off), if any. */
    val recentParentPin: String? get() = heldParentPin?.takeIf { System.currentTimeMillis() < heldParentPinUntil }

    /** Whether child lock is still on here: it can be turned off from the web's Devices page while the app's open. */
    suspend fun refreshKidsLock() {
        val client = client ?: return
        val locked = runCatching { client.me().kidsLock }.getOrNull() ?: return
        if (kidsLock && !locked) {
            kidsLock = false
            loadProfiles()
        } else {
            kidsLock = locked
        }
    }

    /**
     * Locks this device to kids profiles, choosing one: the admin PIN unless it's unlocked here. Returns whether
     * there's more than one kids profile, so the parent should choose which one this device stays on.
     */
    suspend fun lockToKids(pin: String?): Boolean {
        val credentials = credentials ?: return false
        val selection = credentials.client().lockToKids(pin)
        kidsLock = true
        chose(selection, profiles.filter { it.isKids })
        return profiles.size > 1
    }

    /** Releases this device from kids profiles with the admin PIN; every profile is offered again. */
    suspend fun releaseKidsLock(pin: String) {
        val client = client ?: return
        client.releaseKidsLock(pin)
        kidsLock = false
        heldParentPin = pin
        heldParentPinUntil = System.currentTimeMillis() + 60_000
        loadProfiles()
    }

    /** Takes on the profile the server chose, from [list]. */
    private fun chose(selection: ProfileSelection, list: List<Profile>) {
        val credentials = credentials ?: return
        // Straight from one profile to another (the TV's "Who's watching?", a link for someone else): their music,
        // podcasts and Up Next go, as switchProfile's do.
        if (isSwitch(currentProfile?.id, selection.profile.id)) {
            music.stop()
            podcasts.stop()
            upNext = emptyList()
        }
        val updated = credentials.copy(profileToken = selection.token)
        // Saved only for a saved sign-in: one for this launch alone stays that way.
        if (saved) store.saveProfile(selection.token)
        phase = Phase.SignedIn(updated)
        profiles = list.map { it.copy(isCurrent = it.id == selection.profile.id) }
        // Which profiles ask for a PIN depends on who's watching (leaving a kids profile takes the main profile's), so the
        // list is read again as the new profile.
        scope.launch {
            val fresh = runCatching { updated.client().profiles() }.getOrNull() ?: return@launch
            if (fresh.any { it.isCurrent && it.id == selection.profile.id }) profiles = fresh
        }
        watchStateChanged()
    }

    /** Back to choosing who's watching. */
    fun switchProfile() {
        val credentials = credentials ?: return
        // Someone else's music, podcasts and Up Next.
        music.stop()
        podcasts.stop()
        upNext = emptyList()
        forgetWatchlist()
        if (saved) store.saveProfile(null)
        forgetSignIn()
        phase = Phase.SignedIn(credentials.copy(profileToken = null))
        profiles = profiles.map { it.copy(isCurrent = false) }
        // The widget showed someone else's list: it's dropped, and asks who's watching until someone's chosen.
        scope.launch { WatchlistWidget.forget(context) }
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
        // A kids profile has no podcasts.
        if (isKids) {
            podcastStatus = null
            return
        }
        podcastStatus = runCatching { client.podcastStatus() }.getOrNull()
        if (podcastStatus?.available == true) {
            runCatching { client.podcastPreferences() }.onSuccess { podcastPreferences = it }
        }
    }

    suspend fun loadLiveTvStatus() {
        val client = client ?: return
        // A kids profile has no Live TV.
        liveTvStatus = if (isKids) null else runCatching { client.liveTvStatus() }.getOrNull()
    }

    suspend fun loadUpNext() {
        val client = client ?: return
        if (podcastStatus?.available == false || isKids) return
        runCatching { client.upNext() }.onSuccess { upNext = it }
    }

    /** Puts an episode in Up Next: first (Play next), or last. */
    fun addToUpNext(episodeId: String, next: Boolean) {
        val client = client ?: return
        scope.launch {
            runCatching { client.addToUpNext(episodeId, next) }.onSuccess { upNext = it }
            PodcastWidgets.refresh(context)
        }
    }

    fun removeFromUpNext(episodeId: String) {
        val client = client ?: return
        upNext = upNext.filterNot { it.id == episodeId }
        scope.launch {
            runCatching { client.removeFromUpNext(episodeId) }
            PodcastWidgets.refresh(context)
        }
    }

    fun watchStateChanged() {
        watchStateVersion++
        // Watched films and started shows leave the watchlist (and who's watching may have changed).
        refreshWatchlistSoon()
        scope.launch {
            ContinueWatchingWidget.refresh(context)
            PodcastWidgets.refresh(context)
        }
    }

    private var checkingSignIn = false

    /**
     * A request was refused (401). If the server still refuses this sign-in when asked directly, it was signed out
     * there (from the web's Sign-ins list, or a new key): forget it and go back to connecting. One refusal alone could
     * be a server mid-restart. Downloads stay, and what was watched on them is sent once this server's signed in again.
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
                    val kept = downloads.signedOutByServer(refused.address)
                    notifications.forget()
                    forget()
                    signedOutNotice = if (kept) {
                        "This device was signed out. Its downloads are kept, and catch up when it connects to this server again."
                    } else {
                        "This device was signed out."
                    }
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

    /** The server whose error-report setting was last asked for. */
    private var reportsServer: String? = null

    /**
     * The log sends to the server signed in to, and error reports follow that server's setting. Downloads kept from a
     * refused sign-in stay for the same server and go for another.
     */
    private fun signInChanged() {
        val client = client
        LogHub.signedIn(client)
        val address = credentials?.address
        address?.let(downloads::signingIn)
        if (address == null) {
            reportsServer = null
            ErrorReports.stop()
        } else if (address != reportsServer && client != null) {
            reportsServer = address
            scope.launch { ErrorReports.refresh(client, address) }
        }
    }

    init {
        signInChanged()
    }

    companion object {
        /** How long [refreshWatchlistSoon] waits for changes to stop coming. */
        private const val WATCHLIST_SETTLE_MS = 750L

        /**
         * The server's address for away from home, from its [status], as a base URL: every request there carries this
         * sign-in. Null when it has none, it's the [address] already in use, it isn't a Tailscale one (see
         * [FlexarrClient.isAcceptableRemote]), or a different server answers there. A new one is only taken once the
         * server there gives the same id as this one ([SystemStatus.serverId], asked by [idAt] without the sign-in);
         * while nothing answers there, the address [kept] until now stays, to be checked again next launch. Servers
         * before 0.55 have no id, so their address is taken without asking.
         */
        internal suspend fun remoteAddress(
            status: SystemStatus,
            address: String,
            kept: String? = null,
            idAt: suspend (HttpUrl) -> String? = { FlexarrClient.serverIdAt(it) },
            refused: (String) -> Unit = { flexarrLog.warning(it) },
        ): String? {
            val url = status.remoteUrl?.let { FlexarrClient.normalizeAddress(it) } ?: return null
            if (!FlexarrClient.isAcceptableRemote(url)) {
                refused("The server's address for away from home isn't a Tailscale one; not using it")
                return null
            }
            val remote = url.toString().takeIf { it != address } ?: return null
            // Checked when it was kept.
            if (remote == kept) return kept
            val id = status.serverId ?: return remote
            val there = try {
                idAt(url)
            } catch (_: FlexarrException.Unreachable) {
                return kept
            }
            if (there != id) {
                refused("A different server answers at the server's address for away from home; not using it")
                return null
            }
            return remote
        }
    }
}

/** Choosing [chosen] while [current] is watching changes who's watching (choosing at first sign-in doesn't). */
internal fun isSwitch(current: String?, chosen: String): Boolean = current != null && current != chosen
