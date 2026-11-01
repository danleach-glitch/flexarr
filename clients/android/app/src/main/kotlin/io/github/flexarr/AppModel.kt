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
import io.github.flexarr.audio.BookPlayer
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
import io.github.flexarr.downloads.BookDownloads
import io.github.flexarr.downloads.DownloadStore
import io.github.flexarr.logging.ErrorReports
import io.github.flexarr.logging.FlexarrLog
import io.github.flexarr.logging.LogHub
import io.github.flexarr.notifications.PushNotifications
import io.github.flexarr.widget.ContinueWatchingWidget
import io.github.flexarr.widget.PodcastWidgets
import io.github.flexarr.widget.WatchlistWidget
import io.github.flexarr.data.ServerCredentials
import io.github.flexarr.data.GuestTunnelInfo
import io.github.flexarr.core.GuestInvite
import io.github.flexarr.core.GuestJoinProblem
import io.github.flexarr.core.guestJoinProblem
import io.github.flexarr.core.redeemInvite
import io.github.flexarr.guest.GuestTunnel
import io.github.flexarr.guest.TunnelException
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl.Companion.toHttpUrl
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ProcessLifecycleOwner
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
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

    /** The notice is for a guest whose host removed it: there's no home server to show a code for. */
    var signedOutAsGuest = false
        private set

    private var profileList: List<Profile> by mutableStateOf(emptyList())

    var profiles: List<Profile>
        get() = profileList
        private set(value) {
            // Kept on the device, so who's watching (and whether it's a child) is still known offline. Never a list with
            // nobody watching (on its way to "Who's watching?"): offline goes on as the profile last chosen.
            if (value != profileList && value.any { it.isCurrent } && saved && credentials != null) store.saveProfiles(value)
            profileList = value
        }

    /** The profiles have been asked for since signing in (whether or not the server answered). */
    var profilesLoaded by mutableStateOf(false)
        private set

    /**
     * Debug builds started with `--ez noHistory true` play without reporting progress or remembering choices made while
     * playing (subtitles, a show's speed, subtitle size and position), so trying things against the household's server
     * doesn't change anyone's watch state or settings.
     */
    var recordsHistory = true

    /** Where "Join someone’s Flexarr" meets its invite: Flexarr's relay, or a local one in debug builds (`--es pairRelay`). */
    var pairRelay: okhttp3.HttpUrl = io.github.flexarr.core.GuestTvCode.RELAY

    /** Goes up whenever something may have changed what's watched (the player closed, say): screens load again. */
    var watchStateVersion by mutableIntStateOf(0)
        private set

    /** Goes up when the libraries' order changes, so the Libraries tab loads them again. */
    var librariesVersion by mutableIntStateOf(0)

    /** What other devices, the web app and assistants ask this one to play, while the app is in front. */
    val commands = DeviceCommands(context, this, scope)

    /** Listens for commands while the app is in front (any of its screens, the players included), and signed in. */
    fun listenForCommands() {
        // A guest's device isn't one of the household's: nothing sends it commands.
        val client = client?.takeIf { !isGuest }
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
    private var kidsLockState by mutableStateOf(false)

    var kidsLock: Boolean
        get() = kidsLockState
        private set(value) {
            // Remembered, so offline a kids-locked device stays a child's.
            if (value != kidsLockState && saved && credentials != null) store.saveKidsLock(value)
            kidsLockState = value
        }

    /** A child is watching: the kids app (no Settings). */
    val isKids: Boolean get() = currentProfile?.isKids == true

    /** This app has its own sign-in to the server (not its key, Tailscale or the local network), which a lock can stay on. */
    var hasOwnSignIn by mutableStateOf(false)
        private set

    /** A grown-up is watching on this app's own sign-in, the household has a kids profile and this device isn't locked yet. */
    val canLockToKids: Boolean get() = hasOwnSignIn && !isGuest && !isKids && !kidsLock && profiles.any { it.isKids }

    /** The server said this is a family guest's sign-in (servers from 0.57). */
    private var serverSaysGuest by mutableStateOf(false)

    /**
     * Signed in as a family guest, through the tunnel inside the app: one profile, fixed by the invite, and no Cast
     * (a Chromecast can't reach the tunnel).
     */
    val isGuest: Boolean get() = credentials?.isGuest == true || serverSaysGuest

    /** The Tailscale node a guest reaches its host's server through. */
    val tunnel = GuestTunnel(context)

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
        serverSaysGuest = me.isGuest
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
        serverSaysGuest = false
        pairing.clear()
    }

    /** Podcast episodes, playing on under every screen. */
    val podcasts = PodcastPlayer(context, this)

    /** Music, playing on under every screen; it and podcasts take turns. */
    val music = MusicPlayer(context, this)

    /** Audiobooks, playing on under every screen; music, podcasts and books take turns. */
    val books = BookPlayer(context, this)

    /** The book's Now Playing sheet is open. */
    var showsBookPlayer by mutableStateOf(false)

    /** The phone's music Now Playing sheet is open: from the bar, or after a mix starts playing. */
    var showsMusicPlayer by mutableStateOf(false)

    /** Goes up when Play or Shuffle starts music on the TV, which then shows Now Playing over everything. */
    var nowPlayingRequests by mutableIntStateOf(0)

    /** What's new, from the server through Firebase. */
    val notifications = PushNotifications(context)

    /** Films, episodes and podcast episodes kept on this phone to play with no connection. */
    val downloads = DownloadStore(context, this)

    /** Whole audiobooks kept on this phone. */
    val bookDownloads = BookDownloads(context, this)

    /**
     * Offline mode: the server can't be reached (no network, or no answer within a few seconds). The phone opens on
     * Downloads under an Offline bar and the TV says it can't reach the server; cleared by itself once it answers.
     */
    var serverUnreachable by mutableStateOf(false)
        private set

    /** Whether this device has a network at all. */
    private val network = NetworkWatch(context)

    /**
     * The server has answered since signing in, this launch: the Android TV keeps its screens under the offline notice
     * rather than start again from Home when the server's back.
     */
    var hasBeenOnline by mutableStateOf(false)
        private set

    /** While offline, tries the server again every so often (only while the app is in front). */
    private var offlineRetry: Job? = null

    /** How many times offline mode has tried since it began, for backing off. */
    private var retryAttempt = 0

    /** The check under way, which any other asking joins rather than start another. */
    private var connectionCheck: Deferred<Unit>? = null

    /** The profile load under way, which anyone else asking joins. */
    private var profilesLoad: Deferred<Unit>? = null

    /** Whether this profile has podcasts (null: not known, or a server from before them). */
    var podcastStatus: PodcastStatus? by mutableStateOf(null)
        private set

    /**
     * How this profile listens to podcasts (its speed, skips and smart rewind), once loaded. The podcast player keeps
     * the last ones, for an episode played before they load again.
     */
    private var loadedPodcastPreferences: PodcastPreferences? by mutableStateOf(null)
    var podcastPreferences: PodcastPreferences?
        get() = loadedPodcastPreferences
        set(value) {
            loadedPodcastPreferences = value
            value?.let(podcasts::rememberListening)
        }

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

    /** Whether each profile's last Home had New podcasts, so Home skips fetching the episodes when it's switched off. */
    val newPodcastsMemory = io.github.flexarr.core.NewPodcastsMemory()

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
    val needsProfile: Boolean get() = profilesLoaded && !serverUnreachable && credentials?.profileToken == null && profiles.size > 1

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

    /** A family invite opened from a link, a paste or a scan, waiting for "Join Dan's Flexarr as Gran?". */
    var offeredInvite: GuestInvite? by mutableStateOf(null)

    /** Said instead when what was opened isn't a whole invite, or this device is signed in already. */
    var inviteProblem: String? by mutableStateOf(null)

    /**
     * [offeredInvite] came to the code this device showed ("Join someone’s Flexarr"): that screen asks about it, in
     * place of the dialog over every screen.
     */
    var inviteFromCode by mutableStateOf(false)
        private set

    /**
     * Offers [invite] to join (null: a link that isn't a whole invite), [fromCode] when it came to the code this device
     * showed. Nothing happens until it's confirmed: anyone who heard the code could have sent theirs.
     */
    fun offerInvite(invite: GuestInvite?, fromCode: Boolean = false) {
        when {
            invite == null -> inviteProblem = "That invite isn’t complete. Ask for the link again."
            phase is Phase.SignedIn -> inviteProblem = "Sign out in Settings first, then open the invite again."
            else -> {
                offeredInvite = invite
                inviteFromCode = fromCode
                joinProblem = null
            }
        }
    }

    /** Joining with [offeredInvite] is under way (it can take a while: the tunnel joins the host's tailnet first). */
    var joining by mutableStateOf(false)
        private set

    /** Why joining with [offeredInvite] didn't work, for the guest. */
    var joinProblem: String? by mutableStateOf(null)
        private set

    /** Trying [joinProblem]'s invite again could work (it couldn't reach the host): Try again is offered only then. */
    var joinCanRetry by mutableStateOf(false)
        private set

    /** "Join": joins with [offeredInvite], in the app's own scope, so it finishes whatever the screens do meanwhile. */
    fun confirmInvite() {
        val invite = offeredInvite ?: return
        if (joining) return
        joining = true
        joinProblem = null
        scope.launch {
            try {
                join(invite)
                offeredInvite = null
                inviteFromCode = false
            } catch (e: CancellationException) {
                throw e
            } catch (e: GuestJoinException) {
                joinProblem = e.message
                joinCanRetry = e.canRetry
            } catch (e: Exception) {
                joinProblem = "That didn’t work."
                joinCanRetry = true
            } finally {
                joining = false
            }
        }
    }

    /** "Cancel", or the problem said: the invite is let go. */
    fun dismissInvite() {
        if (joining) return
        offeredInvite = null
        inviteFromCode = false
        joinProblem = null
        inviteProblem = null
    }

    /** Leaving as a guest, while the server is told and the tunnel taken down: a new join waits for it. */
    private var leaving: Job? = null

    /**
     * Joins with a family invite: the tunnel joins the host's tailnet with its key, the invite's token is swapped there for
     * this device's own sign-in (locked by the server to the invite's profile), and that's saved. Throws with what to
     * say to the guest when it doesn't work.
     */
    suspend fun join(invite: GuestInvite) {
        val owner = invite.ownerName
        if (invite.isExpired()) throw GuestJoinException(guestJoinProblem(IllegalStateException(), owner, keyRefused = true))
        leaving?.join()
        val info = GuestTunnelInfo(invite.tailnetTarget, invite.hostname, owner)
        val port = withContext(Dispatchers.IO) {
            // Trying this invite again after its sign-in didn't go through: the node joined with its key already (which
            // is spent now), so it starts as that identity. Otherwise afresh: an identity left from an earlier invite
            // would keep the node on that tailnet and ignore this key.
            val joinedBefore = tunnel.hasIdentityFor(info)
            if (!joinedBefore) tunnel.forget()
            tunnel.use(info)
            fun failed(e: TunnelException): Nothing {
                // A refused identity or key is no use again; anything else (the network) leaves it for Try again.
                if (e.needsLogin) tunnel.forget() else tunnel.use(null)
                throw GuestJoinException(guestJoinProblem(e, owner, keyRefused = e.needsLogin))
            }
            try {
                tunnel.start(if (joinedBefore) "" else invite.authKey)
            } catch (e: TunnelException) {
                if (!joinedBefore || !e.needsLogin) failed(e)
                // The tailnet refuses the identity this invite made (the host removed it there): afresh, with the key,
                // once, in case it wasn't spent after all.
                tunnel.forget()
                tunnel.use(info)
                try {
                    tunnel.start(invite.authKey)
                } catch (again: TunnelException) {
                    failed(again)
                }
            }
        }
        val address = "http://127.0.0.1:$port/"
        val redeemed = try {
            FlexarrClient(address.toHttpUrl()).redeemInvite(invite.token)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            // The node stays known to the tailnet (its key is spent), so trying again with the same invite still works.
            withContext(Dispatchers.IO) { tunnel.use(null) }
            throw GuestJoinException(guestJoinProblem(e, owner))
        }
        val credentials = ServerCredentials(address, redeemed.apiKey, guest = info)
        store.save(credentials)
        saved = true
        signedIn(credentials)
        flexarrLog.info("Joined as a guest")
    }

    /** The tunnel came up on [port]: a guest's sign-in follows it there, if it had to move from the one it had. */
    private fun tunnelMoved(port: Int) {
        scope.launch {
            // Started again in the background (music, a download, a widget): it stops again once that's done with it.
            if (!inFront && tunnelIdle?.isActive != true) wentToBackground()
            val now = credentials?.takeIf { it.isGuest } ?: return@launch
            val address = "http://127.0.0.1:$port/"
            if (now.address == address) return@launch
            if (saved) store.saveGuestAddress(address)
            phase = Phase.SignedIn(now.copy(address = address))
        }
    }

    /** Signs in for this launch only, without saving or swapping the key: debug builds started from the command line. */
    fun signInForThisLaunch(address: String, apiKey: String, remote: String? = null) {
        val url = FlexarrClient.normalizeAddress(address) ?: return
        saved = false
        signedIn(ServerCredentials(url.toString(), apiKey, remoteAddress = remote?.let { FlexarrClient.normalizeAddress(it)?.toString() }))
    }

    private fun signedIn(credentials: ServerCredentials) {
        forgetSignIn()
        tunnelProblem = null
        offlineAsGuest = false
        leaveOfflineMode()
        profiles = emptyList()
        profilesLoaded = false
        hasBeenOnline = false
        signedOutNotice = null
        signedOutAsGuest = false
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
        // A guest only ever reaches the server through its tunnel.
        if (credentials.isGuest) return
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
        books.stop()
        bookDownloads.deleteAll(client)
        val told = if (saved) client?.let { client -> scope.launch { downloadsDone.join(); notifications.signedOut(client); runCatching { client.signOut() } } } else null
        forget(tunnelNeededBy = told)
    }

    /**
     * Forgets the sign-in. A guest's tunnel goes too, with its identity on the host's tailnet (its sign-in has gone, so
     * it's no use): once [tunnelNeededBy] has finished telling the server.
     */
    private fun forget(tunnelNeededBy: Job? = null) {
        if (credentials?.isGuest == true || tunnel.isInUse) {
            leaving = scope.launch {
                tunnelNeededBy?.join()
                withContext(Dispatchers.IO) { tunnel.forget() }
            }
        }
        commands.stop()
        forgetSignIn()
        tunnelProblem = null
        offlineAsGuest = false
        leaveOfflineMode()
        music.stop()
        podcasts.stop()
        books.stop()
        podcastStatus = null
        upNext = emptyList()
        forgetWatchlist()
        if (saved) store.clear()
        profiles = emptyList()
        profilesLoaded = false
        hasBeenOnline = false
        phase = Phase.SignedOut
        scope.launch {
            ContinueWatchingWidget.refresh(context)
            PodcastWidgets.refresh(context)
            WatchlistWidget.forget(context)
        }
    }

    /**
     * A guest's tunnel didn't start (or is still trying after a while): its problem screen shows in place of the app,
     * with Try again, Leave and (on a phone) Downloads. Null when it's up, and for everyone who isn't a guest.
     */
    var tunnelProblem: String? by mutableStateOf(null)
        private set

    /** The guest chose Downloads on the tunnel's problem screen: the app opens offline until the tunnel is up again. */
    private var offlineAsGuest = false

    /** "Try again" on the tunnel's problem screen: the start still under way ("Still connecting") goes on, or another. */
    fun retryGuestTunnel() {
        tunnelProblem = null
        if (tunnelStart?.isActive == true) return
        scope.launch { loadProfiles() }
    }

    /** "Downloads" on the tunnel's problem screen: the app, opening on Downloads, which play without the server. */
    fun browseOfflineAsGuest() {
        offlineAsGuest = true
        tunnelProblem = null
        goOffline()
    }

    /**
     * Starts a guest's tunnel, or finds it up: true once it is (and for everyone who isn't a guest). False when it
     * can't, with [tunnelProblem] set (unless the guest chose to stay offline), or the tailnet refused this device
     * ([GuestTunnel.onRefused] signs it out). Waits up to a minute; the problem shows after 15 seconds of trying.
     */
    private suspend fun ensureGuestTunnel(): Boolean {
        credentials?.guest ?: return true
        // One start at a time: whoever else asks meanwhile (Try again, the profile list asked again) waits for it.
        val start = tunnelStart?.takeIf { it.isActive } ?: scope.async { startGuestTunnel() }.also { tunnelStart = it }
        return start.await()
    }

    /** The guest tunnel's start under way, if any ([ensureGuestTunnel]). */
    private var tunnelStart: Deferred<Boolean>? = null

    private suspend fun startGuestTunnel(): Boolean {
        val guest = credentials?.guest ?: return true
        val early = scope.launch {
            // With no network at all that's offline mode now; still nothing after a while, the problem shows.
            for (second in 1..15) {
                delay(1_000)
                if (credentials?.guest != guest) return@launch
                if (network.isOffline) return@launch goOffline()
            }
            if (tunnelProblem == null && !offlineAsGuest && !serverUnreachable && credentials?.guest == guest) tunnelProblem = "Still connecting"
        }
        try {
            withContext(Dispatchers.IO) { tunnel.port() }
            if (credentials?.guest == guest) {
                tunnelProblem = null
                offlineAsGuest = false
            }
            return true
        } catch (e: TunnelException) {
            if (credentials?.guest != guest || e.needsLogin) return false
            flexarrLog.warning("The guest tunnel didn't start: ${e.message}")
            if (offlineAsGuest || network.isOffline) goOffline() else tunnelProblem = e.message ?: "The tunnel didn't start"
            return false
        } finally {
            early.cancel()
        }
    }

    /** Loads who's watching (and the sign-in), or joins the load already under way. */
    suspend fun loadProfiles() = loadProfiles(serverAnswered = false)

    /** [serverAnswered]: it has just answered (coming back online), so it isn't asked again. */
    private suspend fun loadProfiles(serverAnswered: Boolean) {
        profilesLoad?.takeIf { it.isActive }?.let { return it.await() }
        // Its own job, so it finishes even when whatever asked is cancelled (a screen going away).
        val load = scope.async { loadProfilesNow(serverAnswered) }
        profilesLoad = load
        load.await()
    }

    private suspend fun loadProfilesNow(serverAnswered: Boolean) {
        // A guest with no network at all needn't wait for its tunnel to give up.
        if (credentials?.isGuest == true && network.isOffline) return goOffline()
        // A guest reaches the server only through its tunnel: if that can't start, its problem screen says so.
        if (!ensureGuestTunnel()) return
        val client = client ?: return
        try {
            // All at once rather than one after another: the profiles, the sign-in, and whether the server answers.
            val listed = scope.async { runCatching { client.profiles() } }
            val signIn = scope.async { runCatching { client.me() }.getOrNull() }
            // No network, or no answer within a few seconds: offline mode now, not after each request's timeout.
            if (!serverAnswered && !serverAnswers(client)) {
                signIn.cancel()
                goOffline()
                // A slow server may answer after all: the list arriving brings the app back at once.
                scope.launch { if (listed.await().isSuccess) checkConnection(knownReachable = true) }
                return
            }
            val list = listed.await().getOrThrow()
            val me = signIn.await()
            // A token for a profile that's since been removed acts as nobody: forget it and ask again.
            val credentials = credentials
            if (list.none { it.isCurrent } && credentials?.profileToken != null) {
                if (saved) store.saveProfile(null)
                phase = Phase.SignedIn(credentials.copy(profileToken = null))
            }
            profiles = list
            hasBeenOnline = true
            // Locked to kids profiles, the first Who's watching offers Unlock, before anyone is chosen.
            me?.let {
                kidsLock = it.kidsLock
                hasOwnSignIn = it.method == "device"
                serverSaysGuest = it.isGuest
            }
            leaveOfflineMode()
        } catch (e: FlexarrException) {
            // A guest's device the host has removed is turned away (403) whatever it asks: back to joining.
            if (e is FlexarrException.Http && e.guestRemoved && credentials?.isGuest == true) return removedAsGuest()
            // Reached, but the list didn't come: the one kept from last time, so the right profile (a child's) shows.
            if (profiles.isEmpty() && saved) profiles = store.savedProfiles()
            if (e is FlexarrException.Unreachable) goOffline()
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
            books.stop()
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

    /** Back to choosing who's watching. Never for a guest, which the server keeps on its invite's profile. */
    fun switchProfile() {
        val credentials = credentials?.takeIf { !isGuest } ?: return
        // Someone else's music, podcasts and Up Next.
        music.stop()
        podcasts.stop()
        books.stop()
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

    /** This profile's audiobook libraries: none for a kids profile (the server hides them), or a server before 0.58. */
    var audiobookLibraries: List<io.github.flexarr.core.Library> by mutableStateOf(emptyList())
        private set

    val hasAudiobooks: Boolean get() = audiobookLibraries.isNotEmpty()

    suspend fun loadAudiobooks() {
        val client = client ?: return
        if (isKids) {
            audiobookLibraries = emptyList()
            return
        }
        runCatching { client.libraries() }.onSuccess { list -> audiobookLibraries = list.filter { it.type == io.github.flexarr.core.LibraryType.Audiobooks } }
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
            } catch (e: FlexarrException.Http) {
                // A guest's device its host removed: refused everything (403), not just the sign-in.
                if (e.guestRemoved && credentials == refused && refused.isGuest) removedAsGuest()
            } catch (_: FlexarrException.Unauthorized) {
                if (credentials == refused) {
                    // A guest's address is the same loopback one whoever's Flexarr it joins, so its downloads can't wait for it.
                    val kept = if (refused.isGuest) false.also { downloads.signedOut(null) } else downloads.signedOutByServer(refused.address)
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

    /** The host removed this guest's device: its sign-in, tunnel and downloads go, and the connect screen says what to do. */
    private fun removedAsGuest() {
        val credentials = credentials ?: return
        val owner = credentials.guest?.ownerName?.ifEmpty { null }
        // Not kept for coming back: every guest's address is this phone's own loopback one, so another host's Flexarr
        // joined later would look like the same server and take them on.
        downloads.signedOut(null)
        notifications.forget()
        forget()
        signedOutNotice = if (owner != null) "You no longer have access to $owner’s Flexarr." else "You no longer have access to this Flexarr."
        signedOutAsGuest = true
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
        // Offline mode follows the network, and any request that can't reach the server has it checked.
        network.onChange(::networkChanged)
        FlexarrClient.onCouldNotConnect = { scope.launch { connectionTrouble() } }
        FlexarrClient.onAnswered = { scope.launch { serverAnswered() } }
        tunnel.onPort = ::tunnelMoved
        // The host deleted this device from its tailnet: as good as removing it from Flexarr.
        tunnel.onRefused = { scope.launch { if (credentials?.isGuest == true) removedAsGuest() } }
        // A guest's tunnel starts now, off the main thread, so it's up (or on its way) before the first request, which
        // waits for it either way.
        savedSignIn?.guest?.let { guest ->
            tunnel.use(guest)
            scope.launch(Dispatchers.IO) { runCatching { tunnel.start() } }
        }
    }

    /** Back in front: a guest's tunnel is started again if it stopped while the app was away. */
    fun cameToFront() {
        tunnelIdle?.cancel()
        if (credentials?.isGuest == true) scope.launch(Dispatchers.IO) { tunnel.ensureRunning() }
        // Offline mode ends (or starts) as soon as it's known whether the server answers now, and tries again while
        // the app's in front.
        scheduleRetry()
        scope.launch { checkConnection() }
    }

    // Offline mode

    /**
     * Whether the server answers: never with no network (unless [ignoringNetwork], in case that reading is wrong),
     * otherwise within a few seconds.
     */
    private suspend fun serverAnswers(client: FlexarrClient, ignoringNetwork: Boolean = false): Boolean =
        (ignoringNetwork || !network.isOffline) && client.isReachable()

    /** Into offline mode: the profile last known, nobody asked who's watching, and the server tried again now and then. */
    private fun goOffline() {
        if (credentials == null) return
        settleOfflineProfile()
        profilesLoaded = true
        if (!serverUnreachable) {
            flexarrLog.info("Offline: the server can't be reached")
            serverUnreachable = true
            retryAttempt = 0
        }
        scheduleRetry()
    }

    /**
     * Who's watching offline: the list as last known, with the profile last chosen. With nobody chosen, or a device
     * locked to kids profiles, a child if the household has one: offline is never a way out of the kids app.
     */
    private fun settleOfflineProfile() {
        if (saved && profiles.none { it.isCurrent }) store.savedProfiles().takeIf { it.isNotEmpty() }?.let { profiles = it }
        if (saved && store.savedKidsLock()) kidsLock = true
        val current = currentProfile
        if (current != null && (current.isKids || !kidsLock)) return
        if (current == null && profiles.size <= 1) return
        val child = profiles.firstOrNull { it.isKids } ?: return
        profiles = profiles.map { it.copy(isCurrent = it.id == child.id) }
    }

    /** While in front, tries again after 15 seconds, then less and less often, up to every 5 minutes. */
    private fun scheduleRetry() {
        if (!serverUnreachable || !inFront || offlineRetry?.isActive == true) return
        offlineRetry = scope.launch {
            while (true) {
                delay(RETRY_WAITS_MS[retryAttempt.coerceAtMost(RETRY_WAITS_MS.lastIndex)])
                retryAttempt++
                // Every third try asks the server even if the network says there's none, in case that's wrong.
                checkConnection(ignoringNetwork = retryAttempt % 3 == 0)
            }
        }
    }

    private fun leaveOfflineMode() {
        offlineRetry?.cancel()
        offlineRetry = null
        serverUnreachable = false
        retryAttempt = 0
    }

    /**
     * Offline: whether the server answers again (the network changed, the app came to the front, a request was
     * answered, Try again, or now and then), and if it does, back to normal where you are. Online: whether it's still
     * there. One check at a time: anyone asking meanwhile waits for that one. [knownReachable]: something has just been
     * answered, so there's no need to ask.
     */
    suspend fun checkConnection(ignoringNetwork: Boolean = false, knownReachable: Boolean = false) {
        connectionCheck?.takeIf { it.isActive }?.let { return it.await() }
        // Its own job, so it finishes even when whatever asked is cancelled (the retry as offline mode ends, or the TV's
        // offline screen going away).
        val check = scope.async { checkConnectionNow(ignoringNetwork, knownReachable) }
        connectionCheck = check
        check.await()
    }

    private suspend fun checkConnectionNow(ignoringNetwork: Boolean, knownReachable: Boolean) {
        if (credentials == null) return
        if (serverUnreachable) {
            // A guest's server is only there once its tunnel is.
            if (credentials?.isGuest == true && (network.isOffline || !ensureGuestTunnel())) return
            val client = client ?: return
            if (!knownReachable && !serverAnswers(client, ignoringNetwork)) return
            flexarrLog.info("Back online")
            offlineAsGuest = false
            // Who's watching and child lock are read again before the screens come back.
            loadProfiles(serverAnswered = true)
            leaveOfflineMode()
            // Whatever failed while offline loads again.
            watchStateChanged()
        } else if (profilesLoaded) {
            // Answered a moment ago: it's there.
            if (System.currentTimeMillis() - FlexarrClient.lastAnsweredAtMs < 20_000) return
            val client = client ?: return
            if (serverAnswers(client, ignoringNetwork)) return
            // One miss could be a moment's trouble (a busy server): offline only if it misses again shortly after.
            delay(3_000)
            if (credentials == null || serverUnreachable || serverAnswers(client)) return
            goOffline()
        }
    }

    /** A request couldn't reach the server: see whether it's gone (one slow request alone doesn't mean offline). */
    private fun connectionTrouble() {
        if (serverUnreachable || !profilesLoaded || credentials == null) return
        scope.launch { checkConnection() }
    }

    /** A request was answered: offline mode ends now rather than at its next check. */
    private fun serverAnswered() {
        if (!serverUnreachable || credentials == null || connectionCheck?.isActive == true) return
        scope.launch { checkConnection(knownReachable = true) }
    }

    private fun networkChanged(offline: Boolean) {
        if (credentials == null) return
        scope.launch {
            if (offline) {
                // A moment's grace: moving from Wi-Fi to mobile data drops the network briefly.
                delay(2_000)
                if (network.isOffline && profilesLoaded && !serverUnreachable) goOffline()
            } else if (serverUnreachable) {
                // Once the new network has settled.
                delay(1_000)
                checkConnection()
            }
        }
    }

    private var tunnelIdle: Job? = null

    /**
     * In the background: a guest's tunnel stops after a minute, once nothing's using it (music or a podcast playing, a
     * download coming in), so it doesn't keep the phone's radio busy. Anything that needs it later starts it again.
     */
    fun wentToBackground() {
        // Offline mode doesn't keep trying in the background: coming back to the front checks.
        if (!inFront) {
            offlineRetry?.cancel()
            offlineRetry = null
        }
        if (!tunnel.isInUse) return
        tunnelIdle?.cancel()
        tunnelIdle = scope.launch {
            delay(TUNNEL_IDLE_MS)
            while (music.wantsToPlay || podcasts.wantsToPlay || books.wantsToPlay || downloads.isTransferring || bookDownloads.isTransferring) delay(TUNNEL_IDLE_MS)
            // Back in front meanwhile (cameToFront cancels this, but it may not have run yet).
            if (inFront) return@launch
            withContext(Dispatchers.IO) { tunnel.stop() }
        }
    }

    private val inFront: Boolean get() = ProcessLifecycleOwner.get().lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)

    companion object {
        /** How long [refreshWatchlistSoon] waits for changes to stop coming. */
        private const val WATCHLIST_SETTLE_MS = 750L

        /** While offline, how long to wait before each try: 15 seconds at first, then less and less often. */
        private val RETRY_WAITS_MS = longArrayOf(15_000, 30_000, 60_000, 120_000, 300_000)

        /** How long a guest's tunnel stays up with the app in the background and nothing using it. */
        private const val TUNNEL_IDLE_MS = 60_000L

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

/** Joining with an invite didn't work; the message says why, for the guest. */
class GuestJoinException(problem: GuestJoinProblem) : Exception(problem.message) {
    val canRetry = problem.canRetry
}
