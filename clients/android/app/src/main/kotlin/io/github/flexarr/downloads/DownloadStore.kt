package io.github.flexarr.downloads

import java.util.concurrent.TimeUnit
import android.Manifest
import android.content.pm.PackageManager
import io.github.flexarr.core.i18n.t
import io.github.flexarr.logging.FlexarrLog
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import androidx.core.content.edit
import androidx.work.Constraints
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OneTimeWorkRequest
import androidx.work.WorkManager
import androidx.work.workDataOf
import io.github.flexarr.AppModel
import io.github.flexarr.usesTvInterface
import io.github.flexarr.core.CollectionKind
import io.github.flexarr.core.CollectionRules
import io.github.flexarr.core.CollectionStatus
import io.github.flexarr.core.DownloadQuality
import io.github.flexarr.core.DownloadRules
import io.github.flexarr.core.DownloadedShow
import io.github.flexarr.core.DownloadedShows
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.LocalCollection
import io.github.flexarr.core.LocalDownload
import io.github.flexarr.core.LocalPhase
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.OfflineCheck
import io.github.flexarr.core.OfflineDownload
import io.github.flexarr.core.checkOffline
import io.github.flexarr.core.PlaylistSummary
import io.github.flexarr.core.ServerAnswer
import io.github.flexarr.core.deleteDownload
import io.github.flexarr.core.downloadOptions
import io.github.flexarr.core.markDownloadReceived
import io.github.flexarr.core.offlineDownload
import io.github.flexarr.core.podcastAutoDownloads
import io.github.flexarr.core.podcastPlayedDownloads
import io.github.flexarr.core.artistName
import io.github.flexarr.core.reportOfflineProgress
import io.github.flexarr.core.requestDownload
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.ReceiveChannel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.KSerializer
import kotlinx.serialization.builtins.ListSerializer
import okhttp3.Request
import java.io.File
import java.time.Instant
import kotlin.system.measureTimeMillis

private val flexarrLog = FlexarrLog("downloads")

/**
 * Downloads on this phone, as the iPhone app's DownloadStore: asking the server to get them ready, fetching them in the
 * background (carrying on after the app or phone restarts: those asked for by hand as user-initiated jobs on Android 14
 * on, the rest with WorkManager), playing them with no connection, sending
 * what was watched once there's a connection again, fetching the next episode by itself, keeping the newest episodes of
 * podcasts that auto-download, and deleting episodes a day after they're finished and podcast episodes once they're
 * played. Downloads the phone starts by itself
 * wait for Wi-Fi unless Settings says otherwise. Albums and playlists come as collections of track downloads.
 */
class DownloadStore(private val context: Context, private val model: AppModel) {
    private val prefs = context.getSharedPreferences("downloads", Context.MODE_PRIVATE)
    private val work = WorkManager.getInstance(context)
    private val scope = model.scope

    /** [all], with each download found at once by its id or its title: made again whenever the list changes. */
    private var index by mutableStateOf(DownloadIndex(emptyList()))

    /** Every download on the phone, whoever's it is: empty for the moment it takes to read the saved list at launch. */
    var all: List<LocalDownload>
        get() = index.all
        private set(value) {
            // A list that's the same as before changes nothing, so nothing redraws.
            if (value != index.all) index = DownloadIndex(value)
        }

    /** Every album and playlist on the phone, whoever's it is (their tracks are downloads in [all]). */
    var collections: List<LocalCollection> by mutableStateOf(emptyList())
        private set

    /**
     * Collections whose tracks are still being asked of the server, so one whose requests were cut short shows as
     * failed as soon as they stop.
     */
    private var queueing: Set<String> by mutableStateOf(emptySet())

    /** How far each transfer under way has got, kept apart from [all] so the lists don't redraw at every step. */
    private val transferring = mutableStateMapOf<String, Double>()

    /** The profile watching: whose downloads show. Remembered, so they show with no connection too. */
    var profileId: String? by mutableStateOf(prefs.getString(PROFILE, null))
        private set

    /** The size films and episodes come at: Medium unless changed in Settings. */
    var quality: DownloadQuality by mutableStateOf(DownloadQuality.entries.firstOrNull { it.raw == prefs.getString(QUALITY, null) } ?: DownloadQuality.Medium)
        private set

    /** Fetch the next episode by itself. */
    var downloadsNext: Boolean by mutableStateOf(prefs.getBoolean(NEXT, true))
        private set

    /**
     * Android 13 on asks before the app may show notifications, and nothing asked until someone turned on push
     * notifications, so a download's progress (and its Stop download) went unseen. The first download asked for by
     * hand asks ([MainActivity] shows Android's question), once.
     */
    var asksNotificationPermission by mutableStateOf(false)

    private fun offerNotifications() {
        if (Build.VERSION.SDK_INT < 33 || prefs.getBoolean(ASKED_NOTIFICATIONS, false)) return
        prefs.edit().putBoolean(ASKED_NOTIFICATIONS, true).apply()
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            asksNotificationPermission = true
        }
    }

    /** Downloads the phone starts by itself wait for Wi-Fi (on unless turned off in Settings). */
    var automaticOnWiFiOnly: Boolean by mutableStateOf(prefs.getBoolean(WIFI_ONLY, true))
        private set

    var isOnline by mutableStateOf(true)
        private set

    /** On a network that isn't metered, with Data Saver off for Flexarr: what the iPhone app calls Wi-Fi. */
    var isOnWiFi by mutableStateOf(false)
        private set

    private val connectivity = context.getSystemService(ConnectivityManager::class.java)

    /** Data Saver is on and Flexarr isn't allowed round it: Android's Low Data Mode. */
    private var dataSaver = isDataSaverOn()

    /** The default network's, as last heard: kept to work Wi-Fi out again when Data Saver changes. */
    @Volatile private var capabilities: NetworkCapabilities? = null

    /** Podcast episodes whose download was deleted by hand (by episode id): never fetched by themselves again. */
    private var declined: Set<String> = prefs.getStringSet(DECLINED, emptySet())!!.toSet()
        set(value) {
            field = value
            prefs.edit { putStringSet(DECLINED, value) }
        }

    /** Downloads deleted here that the server hasn't heard about yet (it was out of reach): told on the next catch-up. */
    private var unsentDeletes: Set<String> = prefs.getStringSet(UNSENT_DELETES, emptySet())!!.toSet()
        set(value) {
            field = value
            prefs.edit { putStringSet(UNSENT_DELETES, value) }
        }

    private var client: FlexarrClient? = null
    private var reconciling = false

    /** The saved list has been read: nothing is saved before, so it can't be overwritten with an empty one. */
    private var loaded = false

    /** Everything was deleted (signed out) before the saved list had been read: what it held is gone too. */
    private var clearedBeforeLoad = false

    /** Saves waiting to be written, off the main thread; only the newest matters. */
    private val saves = Channel<List<LocalDownload>>(Channel.CONFLATED)
    private val collectionSaves = Channel<List<LocalCollection>>(Channel.CONFLATED)

    /**
     * Downloads were deleted: the list is saved without the rest a long one's write is given, so a list on disk never
     * names files that have gone for longer than it did.
     */
    private val hurry = Channel<Unit>(Channel.CONFLATED)

    /**
     * Downloads that have come (by id) whose server hasn't been told yet. It lets its own copy go as soon as it hears,
     * so it's told only once the list saying they're here is on disk ([saved]). Told at once, as it used to be, an app
     * stopped before the list's next save (up to two seconds off, for a long list) came back to a list without the
     * file and a server without its copy, and the download failed; stopped before the server's told, the server still
     * has the file and it's fetched again. Read and changed on the main thread.
     */
    private val unacknowledged = mutableSetOf<String>()

    /** Reads the saved lists off the main thread, then lets saves through. */
    private val loading: Job = scope.launch {
        val (saved, savedCollections) = withContext(Dispatchers.IO) {
            folder.mkdirs()
            load(File(folder, MANIFEST), LocalDownload.serializer()) to load(File(folder, COLLECTIONS), LocalCollection.serializer())
        }
        if (!clearedBeforeLoad) {
            all = saved
            collections = savedCollections
        }
        loaded = true
        if (clearedBeforeLoad) {
            save()
            saveCollections()
        }
    }

    init {
        // Written one after another, a moment apart, so a burst of changes is one write.
        scope.launch(Dispatchers.IO) {
            saveAsTheyCome(saves, hurry) { list ->
                var written = false
                val took = measureTimeMillis { written = write(list, LocalDownload.serializer(), MANIFEST) }
                if (written) scope.launch { saved(list) }
                took
            }
        }
        scope.launch(Dispatchers.IO) {
            for (list in collectionSaves) {
                write(list, LocalCollection.serializer(), COLLECTIONS)
                delay(SAVE_GAP_MS)
            }
        }
        watchNetwork()
    }

    /** Returns once the saved list has been read (at once, after the first moment of a launch). */
    suspend fun ready() = loading.join()

    // What shows

    /** This profile's downloads, newest first. */
    val downloads: List<LocalDownload> get() = all.filter { it.profileId == profileId }.sortedByDescending { it.createdAtMillis }

    /** Films only: a kind this app doesn't know (from a newer server) isn't filed under them. */
    val films: List<LocalDownload> get() = downloads.filter { it.item.kind == MediaKind.Movie }

    /** Episodes by show (by its id, or its name from servers that don't send one), in episode order; the shows by name. */
    val shows: List<DownloadedShow> get() = DownloadedShows.group(downloads, t("Episodes"))

    /**
     * Shows and seasons (by id) whose Download someone pressed, for each profile: only those say how far they've got,
     * so downloading one episode doesn't turn its show or season into "Downloading". Stop or Remove forgets them.
     */
    private var startedGroups: Map<String, Set<String>> by mutableStateOf(emptyMap())

    private fun startedKey(profile: String) = "$STARTED_GROUPS-$profile"

    private fun startedFor(profile: String): Set<String> =
        startedGroups[profile] ?: prefs.getStringSet(startedKey(profile), emptySet())!!.toSet()

    /** Someone pressed this show's or season's Download (as this profile). */
    fun isStarted(groupId: String): Boolean = profileId?.let { groupId in startedFor(it) } == true

    fun markStarted(groupId: String) {
        val profile = profileId ?: model.currentProfile?.id ?: return
        setStarted(profile, startedFor(profile) + groupId)
    }

    /** Stop or Remove on a group: it, and every other group of these ids, no longer counts as started. */
    fun forgetStarted(groupIds: Set<String>) {
        val profile = profileId ?: return
        val now = startedFor(profile)
        if (now.any { it in groupIds }) setStarted(profile, now - groupIds)
    }

    private fun setStarted(profile: String, ids: Set<String>) {
        startedGroups = startedGroups + (profile to ids)
        prefs.edit { putStringSet(startedKey(profile), ids) }
    }

    /** Every download went: no group counts as started any more. */
    private fun forgetAllStarted() {
        startedGroups = emptyMap()
        val keys = prefs.all.keys.filter { it.startsWith("$STARTED_GROUPS-") }
        prefs.edit { keys.forEach { remove(it) } }
    }

    /** This profile's downloads of a show's episodes, whatever their state. */
    fun episodesOfShow(showId: String): List<LocalDownload> = downloads.filter { it.item.kind == MediaKind.Episode && it.item.seriesId == showId }

    /** Podcast episodes, newest first. */
    val podcastEpisodes: List<LocalDownload> get() = downloads.filter { it.isPodcastEpisode }.sortedByDescending { it.item.addedAt }

    /** This profile's albums and playlists, newest first. */
    val musicCollections: List<LocalCollection> get() = collections.filter { it.profileId == profileId }.sortedByDescending { it.createdAtMillis }

    fun entry(itemId: String): LocalDownload? = index.entry(profileId, itemId)

    /** A title's download, made through whichever of its linked [copies]. */
    fun entry(itemId: String, copies: List<String>): LocalDownload? = entry(itemId) ?: copies.firstNotNullOfOrNull(::entry)

    fun collection(id: String): LocalCollection? = collections.firstOrNull { it.profileId == profileId && it.id == id }

    /** A collection's tracks in its order (those asked for so far). */
    fun tracks(collection: LocalCollection): List<LocalDownload> = collection.trackIds.mapNotNull(::entry)

    fun status(collection: LocalCollection): CollectionStatus =
        CollectionRules.status(collection.trackIds.map(::entry), queueing = collection.id in queueing, progress = ::progress)

    fun isDownloaded(trackId: String): Boolean = entry(trackId)?.isDownloaded == true

    /** Every track of this album (or playlist) is here. */
    fun isCollectionDownloaded(id: String): Boolean = collection(id)?.let { status(it) == CollectionStatus.Downloaded } == true

    fun bytesUsed(collection: LocalCollection): Long = tracks(collection).filter { it.isDownloaded }.sumOf { it.sizeBytes ?: 0 }

    /** A saved picture, by the name a download or collection holds. */
    fun image(name: String?): File? = name?.let { fileIn(folder, it) }?.takeIf { it.exists() }

    fun byId(id: String): LocalDownload? = index.byId(id)

    val bytesUsed: Long get() = all.filter { it.isDownloaded }.sumOf { it.sizeBytes ?: 0 }

    fun file(download: LocalDownload): File? = download.fileName?.let { fileIn(folder, it) }?.takeIf { it.exists() }

    fun image(download: LocalDownload): File? = image(download.imageFileName)

    /** How far it's got being got ready or coming down, 0 to 1: up to the moment, for a transfer under way. */
    fun progress(download: LocalDownload): Double = transferring[download.id] ?: download.progress

    /** A download is coming in right now. */
    val isTransferring: Boolean get() = transferring.isNotEmpty()

    fun isWiFiOnly(download: LocalDownload): Boolean = DownloadRules.isWiFiOnly(download, automaticOnWiFiOnly)

    /** A download may start by itself now: not asked of the server until it can come (see [DownloadNetwork.mayStartAutomatic]). */
    private val mayStartAutomatic: Boolean get() = DownloadNetwork.mayStartAutomatic(isOnline, isOnWiFi, automaticOnWiFiOnly)

    /** Held back until there's Wi-Fi. */
    fun isWaitingForWiFi(download: LocalDownload): Boolean = download.isPending && isWiFiOnly(download) && !isOnWiFi

    // Settings

    fun chooseQuality(quality: DownloadQuality) {
        this.quality = quality
        prefs.edit { putString(QUALITY, quality.raw) }
    }

    fun changeDownloadsNext(on: Boolean) {
        downloadsNext = on
        prefs.edit { putBoolean(NEXT, on) }
        if (on) scope.launch { catchUp() }
    }

    fun changeAutomaticOnWiFiOnly(on: Boolean) {
        automaticOnWiFiOnly = on
        prefs.edit { putBoolean(WIFI_ONLY, on) }
        // The ones on their way by themselves take the new rule (a transfer under way carries on from where it got to).
        all.filter { it.automatic && it.isPending }.forEach { enqueue(it, replace = true) }
        scope.launch { catchUp() }
    }

    // Connecting

    /** The signed-in server and profile, as they change (a null profile keeps the remembered one: offline, say). */
    fun attach(client: FlexarrClient?, profileId: String?) {
        this.client = client
        if (profileId != null && profileId != this.profileId) {
            this.profileId = profileId
            prefs.edit { putString(PROFILE, profileId) }
        }
        scope.launch { catchUp() }
    }

    /**
     * Signed in to [address] (again, or another profile chosen). Downloads kept from a sign-in the server refused stay
     * if it's the same server, and catch up from where they were; for another server they go, as signing out would,
     * whole audiobooks with them (the main profile has the same id on every server, so they'd show as this server's).
     */
    fun signingIn(address: String) {
        val keptFor = prefs.getString(KEPT_FOR, null) ?: return
        prefs.edit { remove(KEPT_FOR) }
        if (keptFor == address) return
        flexarrLog.info("Signed in to another server: deleting the downloads kept from the last one")
        signedOut(null)
        model.bookDownloads.deleteAll(null)
    }

    /**
     * The server refused this device's sign-in (signed out from the web's Sign-ins list, or a new key). The downloads
     * stay, with what was watched offline and not yet sent, for when the device signs in to [address] again; transfers
     * stop until then. True when there was anything to keep: whole audiobooks count, so that with only books here a
     * sign-in to another server still clears them ([signingIn]).
     */
    fun signedOutByServer(address: String): Boolean {
        work.cancelAllWorkByTag(WORK_TAG)
        UserInitiatedTransfers.cancelAll(context)
        transferring.clear()
        client = null
        val books = model.bookDownloads.all.size
        if (all.isEmpty() && books == 0) return false
        prefs.edit { putString(KEPT_FOR, address) }
        flexarrLog.info("Signed out by the server: keeping ${all.size + books} downloads for when it's signed in again")
        return true
    }

    /** The client the transfers use: the store's, or the app's after a restart in the background. */
    internal val currentClient: FlexarrClient? get() = client ?: model.client

    private fun isDataSaverOn() = connectivity.restrictBackgroundStatus == ConnectivityManager.RESTRICT_BACKGROUND_STATUS_ENABLED

    private fun watchNetwork() {
        fun update() {
            val capabilities = capabilities
            val online = capabilities?.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) == true
            val unmetered = capabilities?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) == true ||
                (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && capabilities?.hasCapability(NetworkCapabilities.NET_CAPABILITY_TEMPORARILY_NOT_METERED) == true)
            val saver = isDataSaverOn()
            val wifi = DownloadNetwork.isWiFi(online, unmetered, saver)
            scope.launch {
                val cameBack = (online && !isOnline) || (wifi && !isOnWiFi)
                val saverChanged = saver != dataSaver
                isOnline = online
                isOnWiFi = wifi
                dataSaver = saver
                // Ones on their way by themselves take the new rule (a transfer under way carries on from where it got to).
                if (saverChanged) all.filter { it.automatic && it.isPending }.forEach { enqueue(it, replace = true) }
                if (cameBack) catchUp()
            }
        }
        capabilities = connectivity.getNetworkCapabilities(connectivity.activeNetwork)
        update()
        connectivity.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
            override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) {
                this@DownloadStore.capabilities = capabilities
                update()
            }

            override fun onLost(network: Network) {
                this@DownloadStore.capabilities = null
                update()
            }
        })
        // Data Saver turned on or off, or Flexarr let round it: told only to receivers registered while running.
        ContextCompat.registerReceiver(
            context,
            object : BroadcastReceiver() {
                override fun onReceive(context: Context, intent: Intent) = update()
            },
            IntentFilter(ConnectivityManager.ACTION_RESTRICT_BACKGROUND_CHANGED),
            ContextCompat.RECEIVER_NOT_EXPORTED,
        )
    }

    /**
     * Sends what was watched offline and deletions the server missed, makes sure everything on its way has a transfer,
     * deletes what's finished with, fetches next episodes that are due and keeps podcasts' newest episodes.
     */
    suspend fun catchUp() {
        // One at a time (everything here runs on the main thread): asked again meanwhile, it goes round once more after.
        if (catchingUp) {
            catchUpAgain = true
            return
        }
        catchingUp = true
        try {
            do {
                catchUpAgain = false
                catchUpOnce()
            } while (catchUpAgain)
        } finally {
            catchingUp = false
        }
    }

    private var catchingUp = false
    private var catchUpAgain = false

    private suspend fun catchUpOnce() {
        ready()
        removeFinished()
        val client = currentClient ?: return
        if (!isOnline) return
        for (id in unsentDeletes) {
            try {
                client.deleteDownload(id)
                unsentDeletes = unsentDeletes - id
            } catch (e: FlexarrException.Http) {
                if (e.status == 404) unsentDeletes = unsentDeletes - id
            } catch (_: FlexarrException) {
                break // still out of reach
            }
        }
        // Once for all the plays sent, not once each: every screen listening fetches its titles again.
        var sent = false
        try {
            for (download in all.filter { it.profileId == profileId && it.unsent != null }) {
                val unsent = download.unsent ?: continue
                try {
                    client.reportOfflineProgress(download.item.id, unsent.position, unsent.finished, Instant.ofEpochMilli(unsent.playedAtMillis))
                    update(download.id) { if (it.unsent == unsent) it.copy(unsent = null) else it }
                    sent = true
                } catch (e: FlexarrException.Http) {
                    // The title's gone from the server: there's nothing left to tell it about.
                    if (e.status == 404) update(download.id) { if (it.unsent == unsent) it.copy(unsent = null) else it }
                    else flexarrLog.warning("Couldn't send offline progress", e)
                } catch (e: FlexarrException) {
                    if (e !is FlexarrException.Unreachable) flexarrLog.warning("Couldn't send offline progress", e)
                }
            }
        } finally {
            if (sent) model.watchStateChanged()
        }
        removeFinished() // podcast episodes whose play was waiting to be sent
        removePlayedPodcasts(client)
        // A title saved by an app that didn't know its kind (a podcast episode, kept by an older version) was saved as
        // Unknown and would stay so, filed under Films: what the server says now puts it right, as on iPhone.
        for (download in all.filter { it.profileId == profileId && it.item.kind == MediaKind.Unknown }) {
            val remote = runCatching { client.offlineDownload(download.id) }.getOrNull() ?: continue
            if (remote.item.kind != MediaKind.Unknown) update(download.id) { it.copy(item = remote.item) }
        }
        removeKeptFromProfile(client)
        // A transfer for everything still to come (one that's running already is left alone).
        all.filter { it.isPending }.forEach { enqueue(it, replace = false) }
        downloads.forEach { fetchNext(after = it) }
        reconcilePodcasts()
        // Whole books are kept apart, and catch up with the rest.
        model.bookDownloads.catchUp()
    }

    /**
     * Titles kept here that the profile may no longer see (a parent blocked one, lowered its age limit or hid its library
     * since), or that the server no longer has: the server says which ([checkOffline]) and they go, books too. One whose
     * file is only missing on the server stays, and nothing goes with a server up to 0.62.1 or one out of reach.
     */
    private suspend fun removeKeptFromProfile(client: FlexarrClient) {
        // Asked only as the profile the downloads are kept for (not, say, on Who's watching).
        val profile = profileId?.takeIf { it == model.currentProfile?.id } ?: return
        val asked = (downloads.map { it.item.id } + model.bookDownloads.books.map { it.itemId }).distinct()
        if (asked.isEmpty()) return
        val gone = HashSet<String>()
        for (chunk in asked.chunked(OfflineCheck.MOST_IDS)) {
            val check = try {
                client.checkOffline(chunk)
            } catch (_: FlexarrException) {
                return // out of reach, or an answer not to act on
            } ?: return
            gone += check.toRemove(chunk)
        }
        if (gone.isEmpty()) return
        // Someone else chosen meanwhile: the answer was about the profile asked for.
        if (profileId != profile || model.currentProfile?.id != profile || currentClient !== client) return
        flexarrLog.info("Taking off ${gone.size} downloads this profile may no longer play")
        downloads.filter { it.item.id in gone }.forEach { remove(it.id, tellServer = true) }
        model.bookDownloads.books.filter { it.itemId in gone }.forEach { model.bookDownloads.remove(it.itemId) }
    }

    // Downloading

    /** Downloads a film or episode at the size chosen in Settings: an Original the phone can't play comes at High. */
    suspend fun download(item: ItemSummary) {
        val client = currentClient ?: throw FlexarrException.Unauthorized()
        // Only an Original needs asking about: the smaller sizes always play.
        val options = if (quality == DownloadQuality.Original) runCatching { client.downloadOptions(item.id) }.getOrNull() else null
        val chosen = DownloadRules.quality(quality, options)
        download(item.id, chosen, automatic = false)
    }

    /** Asks the server to get a title ready at this size, then fetches it when it is. Already here or on its way: nothing to do. */
    suspend fun download(itemId: String, quality: DownloadQuality, automatic: Boolean) {
        ready()
        val client = currentClient ?: throw FlexarrException.Unauthorized()
        val profileId = profileId ?: model.currentProfile?.id ?: throw FlexarrException.Unauthorized()
        entry(itemId)?.let { existing ->
            if (existing.phase != LocalPhase.Failed) return
            delete(existing.id)
        }
        val remote = client.requestDownload(itemId, quality)
        if (!automatic) offerNotifications()
        val local = LocalDownload(
            id = remote.id,
            profileId = profileId,
            quality = quality,
            item = remote.item,
            markers = remote.markers,
            durationSeconds = remote.durationSeconds,
            nextId = remote.nextId,
            progress = remote.progress,
            sizeBytes = remote.sizeBytes,
            automatic = automatic,
            createdAtMillis = System.currentTimeMillis(),
        )
        all = all.filterNot { it.id == remote.id } + local
        save()
        flexarrLog.info("Downloading ${remote.item.title} at ${quality.raw}")
        apply(remote)
        enqueue(local, replace = true)
        fetchImage(local, client)?.let { name -> update(local.id) { it.copy(imageFileName = name) } }
    }

    /** Podcast episodes come as they are. */
    suspend fun downloadEpisode(episodeId: String) = download(episodeId, DownloadQuality.Original, automatic = false)

    /**
     * Gives a download a transfer. One asked for by hand runs as a user-initiated job on Android 14 on, when Android
     * takes one (the app's on screen, or a notification's button was just pressed); the rest go to WorkManager. [replace]
     * starts a new one; otherwise one that's waiting or running already is left alone.
     */
    private fun enqueue(download: LocalDownload, replace: Boolean) {
        if (!replace && UserInitiatedTransfers.isScheduled(context, download.id)) return
        if (replace && !download.automatic && UserInitiatedTransfers.schedule(context, download)) {
            work.cancelUniqueWork(workName(download.id))
            return
        }
        if (replace) UserInitiatedTransfers.cancel(context, download.id)
        work.enqueueUniqueWork(workName(download.id), if (replace) ExistingWorkPolicy.REPLACE else ExistingWorkPolicy.KEEP, request(download))
    }

    private fun request(download: LocalDownload, waits: Int = 0): OneTimeWorkRequest {
        val network = if (DownloadNetwork.needsUnmetered(isWiFiOnly(download), download.automatic, dataSaver)) NetworkType.UNMETERED else NetworkType.CONNECTED
        return OneTimeWorkRequestBuilder<DownloadWorker>()
            .setInputData(workDataOf(DownloadWorker.ID to download.id, DownloadWorker.WAITS to waits))
            .setConstraints(Constraints.Builder().setRequiredNetworkType(network).setRequiresStorageNotLow(true).build())
            .apply { if (waits > 0) setInitialDelay(DownloadRules.laterMillis(waits - 1), TimeUnit.MILLISECONDS) }
            .addTag(WORK_TAG)
            .build()
    }

    /**
     * A transfer WorkManager ran ended with [id] still waiting its turn on the server, having come back [waits] times
     * before: it comes back in a few minutes ([DownloadRules.laterMillis]), in place of the one ending. Not if the
     * download's gone, arrived, or a user-initiated job has it now.
     */
    internal fun later(id: String, waits: Int) {
        val download = byId(id)?.takeIf { it.isPending } ?: return
        if (UserInitiatedTransfers.isScheduled(context, id)) return
        work.enqueueUniqueWork(workName(id), ExistingWorkPolicy.REPLACE, request(download, waits + 1))
    }

    /** What the server says about a download; true when its file should be fetched now. Called by the transfers too. */
    internal fun apply(remote: OfflineDownload): Boolean {
        val local = byId(remote.id) ?: return false
        return when (val answer = DownloadRules.answer(local, remote)) {
            is ServerAnswer.Preparing -> {
                update(remote.id) { if (it.phase == LocalPhase.Downloading) it else it.copy(phase = LocalPhase.Preparing, progress = answer.progress) }
                false
            }
            ServerAnswer.Fetch -> {
                update(remote.id) { it.copy(sizeBytes = remote.sizeBytes ?: it.sizeBytes, nextId = remote.nextId, markers = remote.markers, remoteFileName = remote.fileName ?: it.remoteFileName) }
                true
            }
            ServerAnswer.Forget -> {
                remove(remote.id, tellServer = false)
                false
            }
            is ServerAnswer.Failed -> {
                failed(remote.id, answer.message)
                false
            }
            ServerAnswer.None -> false
        }
    }

    internal fun progressed(id: String, fraction: Double) {
        val download = byId(id) ?: return
        transferring[id] = fraction
        // The list itself only hears when it starts coming down: screens read the fraction with [progress], and it isn't
        // saved, as an interrupted transfer works it out again.
        if (download.phase != LocalPhase.Downloading) update(id) { it.copy(phase = LocalPhase.Downloading, progress = fraction) }
    }

    internal fun arrived(id: String, name: String, bytes: Long) {
        transferring.remove(id)
        update(id) { it.copy(phase = LocalPhase.Downloaded, fileName = name, sizeBytes = bytes, progress = 1.0, failure = null) }
        flexarrLog.info("Downloaded $id")
        val here = byId(id) ?: return // deleted as it came: the server was told that
        // The server is told once the list saying so is saved ([saved]).
        unacknowledged += id
        fetchNext(here)
    }

    /**
     * [list] is on disk: the server is told about the downloads in it that have come since the last save, and lets
     * its copies go.
     */
    private fun saved(list: List<LocalDownload>) {
        val here = DownloadRules.savedArrivals(unacknowledged, list)
        if (here.isEmpty()) return
        unacknowledged -= here
        val client = currentClient ?: return
        here.forEach { id -> scope.launch { runCatching { client.markDownloadReceived(id) } } }
    }

    internal fun failed(id: String, message: String) {
        transferring.remove(id)
        update(id) { it.copy(phase = LocalPhase.Failed, failure = message) }
    }

    /**
     * The picture a download shows, saved beside it: a film's backdrop, an episode's still, a podcast episode's artwork,
     * or a track's album cover (one file for the whole album, kept as long as anything uses it).
     */
    private suspend fun fetchImage(download: LocalDownload, client: FlexarrClient): String? {
        val art = download.item.artwork
        if (download.item.kind == MediaKind.Track) {
            val name = CollectionRules.coverName(download.item.parentId ?: download.item.id)
            return if (image(name) != null) name else fetchImage(art.poster, name, 600, client)
        }
        val path = when (download.item.kind) {
            MediaKind.Episode -> art.thumb ?: art.backdrop
            MediaKind.PodcastEpisode -> art.thumb ?: art.poster
            else -> art.backdrop ?: art.poster
        }
        return fetchImage(path, "${safeName(download.id)}.jpg", if (download.isPodcastEpisode) 400 else 600, client)
    }

    /** Saves the picture at [path] as [name] in the downloads folder; null when there's none to be had. */
    private suspend fun fetchImage(path: String?, name: String, width: Int, client: FlexarrClient): String? {
        val url = client.imageUrl(path, width) ?: return null
        return withContext(Dispatchers.IO) {
            runCatching {
                FlexarrClient.sharedHttp.newCall(Request.Builder().url(url).build()).execute().use { response ->
                    if (!response.isSuccessful) return@runCatching null
                    fileIn(folder, name)!!.outputStream().use { response.body.byteStream().copyTo(it) }
                    name
                }
            }.getOrNull()
        }
    }

    // Albums and playlists

    /**
     * Downloads an album: a collection under its cover, and each track in order. Asking again for one that's here (or
     * partly here) fetches what's missing.
     */
    suspend fun download(album: ItemSummary, tracks: List<ItemSummary>) {
        ready()
        val profileId = profileId ?: model.currentProfile?.id ?: throw FlexarrException.Unauthorized()
        val collection = (collection(album.id) ?: LocalCollection(
            id = album.id,
            profileId = profileId,
            kind = CollectionKind.Album,
            title = album.title,
            subtitle = album.artistName,
            accentColor = album.accentColor,
            createdAtMillis = System.currentTimeMillis(),
        )).let { it.copy(album = album, imageFileName = it.imageFileName ?: CollectionRules.coverName(album.id)) }
        download(collection, tracks, album.artwork.poster)
    }

    /** Downloads a playlist: its tracks as they are now, under its chosen cover (or its tracks' covers, as a mosaic). */
    suspend fun download(playlist: PlaylistSummary, tracks: List<ItemSummary>) {
        ready()
        val profileId = profileId ?: model.currentProfile?.id ?: throw FlexarrException.Unauthorized()
        val collection = (collection(playlist.id) ?: LocalCollection(
            id = playlist.id,
            profileId = profileId,
            kind = CollectionKind.Playlist,
            title = playlist.name,
            accentColor = tracks.firstOrNull()?.accentColor,
            createdAtMillis = System.currentTimeMillis(),
        )).let { it.copy(title = playlist.name, imageFileName = it.imageFileName ?: playlist.cover?.let { CollectionRules.playlistCoverName(playlist.id) }) }
        download(collection, tracks, playlist.cover)
    }

    private suspend fun download(collection: LocalCollection, tracks: List<ItemSummary>, coverPath: String?) {
        val client = currentClient ?: throw FlexarrException.Unauthorized()
        val saved = collection.copy(trackIds = tracks.map { it.id })
        collections = collections.filterNot { it.id == saved.id && it.profileId == saved.profileId } + saved
        saveCollections()
        queueing = queueing + saved.id
        try {
            flexarrLog.info("Downloading ${saved.title}: ${tracks.size} tracks")
            saved.imageFileName?.takeIf { image(it) == null }?.let { fetchImage(coverPath, it, 600, client) }
            if (saved.kind == CollectionKind.Album && saved.estimatedBytes == null) {
                runCatching { client.downloadOptions(saved.id).firstOrNull()?.estimatedBytes }.getOrNull()?.let { estimate ->
                    updateCollection(saved.id) { it.copy(estimatedBytes = estimate) }
                }
            }
            // Tracks have no sizes: each comes as it is (or as AAC, when the phone couldn't play its file).
            for (track in tracks) download(track.id, DownloadQuality.Original, automatic = false)
        } finally {
            queueing = queueing - saved.id
        }
    }

    /** Deletes an album or playlist and its tracks, except tracks another of the profile's collections still holds. */
    fun removeCollection(id: String) {
        val collection = collection(id) ?: return
        CollectionRules.tracksToDelete(collection, collections).forEach { trackId -> entry(trackId)?.let { remove(it.id, tellServer = true) } }
        collections = collections.filterNot { it.id == collection.id && it.profileId == collection.profileId }
        saveCollections()
        removeImageIfUnused(collection.imageFileName)
    }

    /** A picture goes once nothing shows it any more (an album's cover is shared by its tracks and the album). */
    private fun removeImageIfUnused(name: String?) {
        if (name == null || all.any { it.imageFileName == name } || collections.any { it.imageFileName == name }) return
        fileIn(folder, name)?.delete()
    }

    // Next episodes and podcasts

    /**
     * Keeps one episode ahead: when one's downloaded or watched, fetch the next by itself, at the same size. Kept to
     * Wi-Fi, it isn't asked for until there's some (coming back to Wi-Fi catches up).
     */
    private fun fetchNext(after: LocalDownload) {
        if (!downloadsNext) return
        val next = DownloadRules.nextToFetch(after, all) ?: return
        if (currentClient == null || !mayStartAutomatic) return
        scope.launch { runCatching { download(next, after.quality, automatic = true) }.onFailure { flexarrLog.warning("Couldn't fetch the next episode", it) } }
    }

    /**
     * Holds what the server says the phone should: the newest unplayed episodes of each podcast that auto-downloads.
     * Missing ones are fetched, once there's Wi-Fi when Settings keeps them to it; ones that came by themselves and
     * aren't wanted any more (played, pushed out by newer ones) go, unless they're part-heard. Not on a TV, which has no
     * podcasts or downloads, though its profile may have episodes downloading on the phone.
     */
    suspend fun reconcilePodcasts() {
        if (context.usesTvInterface) return
        val client = currentClient ?: return
        if (!isOnline || reconciling) return
        reconciling = true
        try {
            val wanted = try {
                client.podcastAutoDownloads()
            } catch (_: FlexarrException) {
                return // a server before podcasts, or out of reach
            }
            val ids = wanted.map { it.episodeId }.toSet()
            downloads.filter { it.isPodcastEpisode && it.automatic && it.item.id !in ids }.forEach {
                if (it.unsent == null && (it.finishedAtMillis != null || it.position == 0.0)) remove(it.id, tellServer = true)
            }
            if (!mayStartAutomatic) return
            for (episode in wanted) {
                if (entry(episode.episodeId) != null || episode.episodeId in declined) continue
                runCatching { download(episode.episodeId, DownloadQuality.Original, automatic = true) }
                    .onFailure { flexarrLog.warning("Couldn't fetch a podcast episode by itself", it) }
            }
        } finally {
            reconciling = false
        }
    }

    // Deleting

    private fun removeFinished() =
        DownloadRules.finishedWith(all, System.currentTimeMillis(), model.podcasts::isPlayingFile).forEach { remove(it.id, tellServer = true) }

    /**
     * Podcast episodes played since they were downloaded, wherever (on the web, say, or marked played): the server says
     * which, and they go, unless one's playing here or has a play here still to send.
     */
    private suspend fun removePlayedPodcasts(client: FlexarrClient) {
        if (podcastEpisodes.isEmpty()) return
        val played = try {
            client.podcastPlayedDownloads().map { it.episodeId }.toSet()
        } catch (_: FlexarrException) {
            return // a server before 0.56, or out of reach
        }
        podcastEpisodes.filter { it.item.id in played && it.unsent == null && !model.podcasts.isPlayingFile(it.item.id) }
            .forEach { remove(it.id, tellServer = true) }
    }

    /**
     * Marked played or unplayed elsewhere: the download says the same, finished or not started, with no place of its own
     * left to send. (A played podcast episode's download then goes, in [markedPlayed].)
     */
    fun marked(itemId: String, played: Boolean) {
        val download = entry(itemId) ?: return
        update(download.id) {
            it.copy(position = 0.0, unsent = null, finishedAtMillis = if (played) it.finishedAtMillis ?: System.currentTimeMillis() else null)
        }
    }

    /** Marked played here: a podcast episode's download goes at once, or a show's when all its episodes were. */
    fun markedPlayed(itemId: String) {
        podcastEpisodes.filter { (it.item.id == itemId || it.item.parentId == itemId) && !model.podcasts.isPlayingFile(it.item.id) }
            .forEach { remove(it.id, tellServer = true) }
    }

    /** Deleted by hand. A podcast episode's is never fetched by itself again (the server is told, and this phone remembers). */
    fun delete(id: String) {
        byId(id)?.takeIf { it.isPodcastEpisode }?.let { declined = declined + it.item.id }
        remove(id, tellServer = true)
    }

    private fun remove(id: String, tellServer: Boolean) {
        val download = byId(id) ?: return
        work.cancelUniqueWork(workName(id))
        UserInitiatedTransfers.cancel(context, id)
        transferring.remove(id)
        unacknowledged.remove(id)
        download.fileName?.let { fileIn(folder, it)?.delete() }
        fileIn(folder, "${safeName(id)}.part")?.delete()
        all = all.filterNot { it.id == id }
        save()
        hurry.trySend(Unit)
        removeImageIfUnused(download.imageFileName)
        if (!tellServer) return
        // The server drops its copy (and won't offer a podcast episode again); out of reach, it's told later.
        unsentDeletes = unsentDeletes + id
        val client = currentClient ?: return
        scope.launch {
            try {
                client.deleteDownload(id)
                unsentDeletes = unsentDeletes - id
            } catch (e: FlexarrException.Http) {
                if (e.status == 404) unsentDeletes = unsentDeletes - id
            } catch (_: FlexarrException) {
            }
        }
    }

    /** Every download goes (Settings' Delete all). What was watched offline is sent first, while it can be. */
    fun deleteAll() {
        model.bookDownloads.deleteAll()
        forgetAllStarted()
        if (!loaded) clearedBeforeLoad = true
        val client = currentClient
        val pending = all.mapNotNull { download -> download.unsent?.let { download.item.id to it } }
        all.map { it.id }.forEach { remove(it, tellServer = true) }
        val pictures = collections.mapNotNull { it.imageFileName }
        collections = emptyList()
        saveCollections()
        pictures.forEach(::removeImageIfUnused)
        if (client != null && pending.isNotEmpty()) {
            scope.launch {
                pending.forEach { (itemId, unsent) ->
                    runCatching { client.reportOfflineProgress(itemId, unsent.position, unsent.finished, Instant.ofEpochMilli(unsent.playedAtMillis)) }
                }
            }
        }
    }

    /**
     * Signing out: what was watched offline is sent and the server told to drop its copies, with the sign-in that still
     * works, then every download goes, whoever's it was. The returned job ends once the server's been told.
     */
    fun signedOut(client: FlexarrClient?): Job {
        val pending = all.mapNotNull { download -> download.unsent?.let { download.item.id to it } }
        val ids = all.map { it.id }
        work.cancelAllWorkByTag(WORK_TAG)
        UserInitiatedTransfers.cancelAll(context)
        if (!loaded) clearedBeforeLoad = true
        folder.listFiles()?.forEach { if (it.name != MANIFEST && it.name != COLLECTIONS) it.delete() }
        all = emptyList()
        collections = emptyList()
        transferring.clear()
        unacknowledged.clear()
        save()
        hurry.trySend(Unit)
        saveCollections()
        profileId = null
        unsentDeletes = emptySet()
        prefs.edit { remove(PROFILE).remove(KEPT_FOR) }
        forgetAllStarted()
        this.client = null
        return scope.launch {
            if (client == null) return@launch
            pending.forEach { (itemId, unsent) ->
                runCatching { client.reportOfflineProgress(itemId, unsent.position, unsent.finished, Instant.ofEpochMilli(unsent.playedAtMillis)) }
            }
            ids.forEach { runCatching { client.deleteDownload(it) } }
        }
    }

    // Playing

    /** Watched or heard to [position] (or the end) from a download: kept here and sent when the server can be reached. */
    fun record(itemId: String, position: Double, finished: Boolean) {
        val download = entry(itemId) ?: return
        update(download.id) { it.recorded(position, finished, System.currentTimeMillis()) }
        if (finished) byId(download.id)?.let(::fetchNext)
        // Playing records every few seconds: sent at once when it's finished, else at most once a minute (what's
        // recorded meanwhile goes with it, as catching up sends the latest).
        if (finished) {
            scope.launch { catchUp() }
        } else if (recordedCatchUp?.isActive != true) {
            val wait = (lastRecordedCatchUp + RECORDED_CATCH_UP_MS - System.currentTimeMillis()).coerceAtLeast(0)
            recordedCatchUp = scope.launch {
                delay(wait)
                lastRecordedCatchUp = System.currentTimeMillis()
                catchUp()
            }
        }
    }

    private var recordedCatchUp: Job? = null
    private var lastRecordedCatchUp = 0L

    /** The next episode of a show, if it's downloaded too. */
    fun nextDownloaded(after: LocalDownload): LocalDownload? = after.nextId?.let(::entry)?.takeIf { it.isDownloaded }

    /** The next podcast episode of the same show that's here and unplayed, oldest first after this one. */
    fun nextDownloadedEpisode(after: ItemSummary): LocalDownload? =
        podcastEpisodes
            .filter { it.isDownloaded && it.finishedAtMillis == null && it.item.id != after.id && it.item.seriesId == after.seriesId && (it.item.addedAt ?: "") > (after.addedAt ?: "") }
            .minByOrNull { it.item.addedAt ?: "" }

    // Saving

    private fun update(id: String, save: Boolean = true, change: (LocalDownload) -> LocalDownload) {
        if (all.none { it.id == id }) return
        all = all.map { if (it.id == id) change(it) else it }
        if (save) save()
    }

    private fun updateCollection(id: String, change: (LocalCollection) -> LocalCollection) {
        if (collections.none { it.id == id && it.profileId == profileId }) return
        collections = collections.map { if (it.id == id && it.profileId == profileId) change(it) else it }
        saveCollections()
    }

    /** Saves the list as it is now: written off the main thread a moment later, with whatever else changed meanwhile. */
    private fun save() {
        if (loaded) saves.trySend(all)
    }

    private fun saveCollections() {
        if (loaded) collectionSaves.trySend(collections)
    }

    /** True when the list reached disk. */
    private fun <T> write(list: List<T>, serializer: KSerializer<T>, name: String): Boolean =
        runCatching {
            val text = FlexarrClient.json.encodeToString(ListSerializer(serializer), list)
            val temporary = File(folder, "$name.tmp")
            temporary.writeText(text)
            temporary.renameTo(File(folder, name))
        }.onFailure { flexarrLog.error("Couldn't save $name", it) }.getOrDefault(false)

    private val folder: File get() = folder(context)

    companion object {
        private const val TAG = "Flexarr"
        private const val MANIFEST = "downloads.json"
        private const val COLLECTIONS = "collections.json"
        private const val PROFILE = "profile"
        private const val QUALITY = "quality"
        private const val NEXT = "next"
        private const val WIFI_ONLY = "wifiOnly"
        private const val ASKED_NOTIFICATIONS = "askedNotifications"
        private const val DECLINED = "declinedEpisodes"
        private const val UNSENT_DELETES = "unsentDeletes"
        private const val STARTED_GROUPS = "startedGroups"
        /** The server whose downloads were kept when it refused this device's sign-in. */
        private const val KEPT_FOR = "keptFor"
        private const val WORK_TAG = "flexarr-download"
        private const val SAVE_GAP_MS = 250L
        private const val MOST_SAVE_GAP_MS = 2_000L
        private const val SAVE_REST = 10
        private const val RECORDED_CATCH_UP_MS = 60_000L

        /**
         * The saved list, keeping every entry that reads (one an older or newer app wrote in a way this one can't is
         * dropped). When anything can't be read, the file is first copied aside, so the next save can't overwrite what
         * was there, as the iPhone app's SavedList.
         */
        internal fun <T> load(file: File, serializer: KSerializer<T>, unreadable: (count: Int, keptAs: File?) -> Unit = ::reportUnreadable): List<T> {
            if (!file.exists()) return emptyList()
            val elements = runCatching { FlexarrClient.json.parseToJsonElement(file.readText()) as kotlinx.serialization.json.JsonArray }.getOrNull()
            val items = elements?.mapNotNull { runCatching { FlexarrClient.json.decodeFromJsonElement(serializer, it) }.getOrNull() }.orEmpty()
            val dropped = elements?.let { it.size - items.size } ?: 1
            if (dropped > 0) {
                val stamp = Instant.now().toString().replace(":", "-")
                val copy = File(file.parentFile, "${file.nameWithoutExtension}.unreadable-$stamp.json")
                unreadable(dropped, runCatching { file.copyTo(copy) }.getOrNull())
            }
            return items
        }

        /**
         * How long to leave after a save of the downloads list that took [writeMillis] before the next. A short list is
         * written in a few milliseconds and saved again a moment later, as it always was. A thousand tracks' takes long
         * enough that saving it four times a second, as the tracks arrive, kept the phone's storage busy throughout: it
         * rests ten times as long as the write took, up to two seconds.
         */
        internal fun saveGapMillis(writeMillis: Long): Long = (writeMillis * SAVE_REST).coerceIn(SAVE_GAP_MS, MOST_SAVE_GAP_MS)

        /**
         * Writes each list sent to [saves] with [write] (which says how long it took), one after another: the first at
         * once, then only the newest of whatever came during the rest after a write ([saveGapMillis]). A word on
         * [hurry] (downloads were deleted) ends that rest early, though never sooner than the moment every save
         * leaves.
         */
        internal suspend fun <T> saveAsTheyCome(saves: ReceiveChannel<T>, hurry: ReceiveChannel<Unit>, write: suspend (T) -> Long) {
            for (list in saves) {
                hurry.tryReceive() // asked for before this write, which is it
                val took = write(list)
                delay(SAVE_GAP_MS)
                // A long list takes a while to write: the longer it took, the longer before the next, unless files
                // were deleted meanwhile.
                withTimeoutOrNull(saveGapMillis(took) - SAVE_GAP_MS) { hurry.receive() }
            }
        }

        private fun reportUnreadable(count: Int, keptAs: File?) {
            flexarrLog.error("Couldn't read $count saved download entries; the file was kept as ${keptAs?.name ?: "nothing"}")
        }

        /** Out of backups: the files are on the server already. */
        fun folder(context: Context): File = File(context.noBackupFilesDir, "downloads")

        /** A part of a file name made from what the server says (an id, an extension): letters, digits, `_` and `-` only. */
        fun safeName(part: String): String = part.replace(Regex("[^A-Za-z0-9_-]"), "_")

        /** [name] in [folder], or null if it would be anywhere else (a name kept from before can't be trusted blindly). */
        fun fileIn(folder: File, name: String): File? =
            File(folder, name).takeIf { it.canonicalFile.parentFile == folder.canonicalFile }

        fun workName(id: String) = "download-$id"
    }
}
