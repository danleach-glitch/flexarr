package io.github.flexarr.downloads

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
import androidx.work.WorkManager
import androidx.work.workDataOf
import io.github.flexarr.AppModel
import io.github.flexarr.core.CollectionKind
import io.github.flexarr.core.CollectionRules
import io.github.flexarr.core.CollectionStatus
import io.github.flexarr.core.DownloadQuality
import io.github.flexarr.core.DownloadRules
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.LocalCollection
import io.github.flexarr.core.LocalDownload
import io.github.flexarr.core.LocalPhase
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.OfflineDownload
import io.github.flexarr.core.PlaylistSummary
import io.github.flexarr.core.ServerAnswer
import io.github.flexarr.core.deleteDownload
import io.github.flexarr.core.downloadOptions
import io.github.flexarr.core.markDownloadReceived
import io.github.flexarr.core.offlineDownload
import io.github.flexarr.core.podcastAutoDownloads
import io.github.flexarr.core.artistName
import io.github.flexarr.core.reportOfflineProgress
import io.github.flexarr.core.requestDownload
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.KSerializer
import kotlinx.serialization.builtins.ListSerializer
import okhttp3.Request
import java.io.File
import java.time.Instant

private val flexarrLog = FlexarrLog("downloads")

/**
 * Downloads on this phone, as the iPhone app's DownloadStore: asking the server to get them ready, fetching them in the
 * background (carrying on after the app or phone restarts: those asked for by hand as user-initiated jobs on Android 14
 * on, the rest with WorkManager), playing them with no connection, sending
 * what was watched once there's a connection again, fetching the next episode by itself, keeping the newest episodes of
 * podcasts that auto-download, and deleting episodes a day after they're finished. Downloads the phone starts by itself
 * wait for Wi-Fi unless Settings says otherwise. Albums and playlists come as collections of track downloads.
 */
class DownloadStore(private val context: Context, private val model: AppModel) {
    private val prefs = context.getSharedPreferences("downloads", Context.MODE_PRIVATE)
    private val work = WorkManager.getInstance(context)
    private val scope = model.scope

    /** Every download on the phone, whoever's it is: empty for the moment it takes to read the saved list at launch. */
    var all: List<LocalDownload> by mutableStateOf(emptyList())
        private set

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
            for (list in saves) {
                write(list, LocalDownload.serializer(), MANIFEST)
                delay(SAVE_GAP_MS)
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

    val films: List<LocalDownload> get() = downloads.filter { it.item.kind != MediaKind.Episode && !it.isPodcastEpisode && it.item.kind != MediaKind.Track }

    /** Episodes by show, in episode order; the shows by name. */
    val shows: List<Pair<String, List<LocalDownload>>>
        get() = downloads.filter { it.item.kind == MediaKind.Episode }
            .groupBy { it.item.seriesTitle ?: "Episodes" }
            .map { (show, episodes) -> show to episodes.sortedWith(compareBy({ it.item.seasonNumber ?: 0 }, { it.item.episodeNumber ?: 0 })) }
            .sortedBy { it.first }

    /** Podcast episodes, newest first. */
    val podcastEpisodes: List<LocalDownload> get() = downloads.filter { it.isPodcastEpisode }.sortedByDescending { it.item.addedAt }

    /** This profile's albums and playlists, newest first. */
    val musicCollections: List<LocalCollection> get() = collections.filter { it.profileId == profileId }.sortedByDescending { it.createdAtMillis }

    fun entry(itemId: String): LocalDownload? = all.firstOrNull { it.profileId == profileId && it.item.id == itemId }

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

    fun byId(id: String): LocalDownload? = all.firstOrNull { it.id == id }

    val bytesUsed: Long get() = all.filter { it.isDownloaded }.sumOf { it.sizeBytes ?: 0 }

    fun file(download: LocalDownload): File? = download.fileName?.let { fileIn(folder, it) }?.takeIf { it.exists() }

    fun image(download: LocalDownload): File? = image(download.imageFileName)

    /** How far it's got being got ready or coming down, 0 to 1: up to the moment, for a transfer under way. */
    fun progress(download: LocalDownload): Double = transferring[download.id] ?: download.progress

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
     * if it's the same server, and catch up from where they were; for another server they go, as signing out would.
     */
    fun signingIn(address: String) {
        val keptFor = prefs.getString(KEPT_FOR, null) ?: return
        prefs.edit { remove(KEPT_FOR) }
        if (keptFor == address) return
        flexarrLog.info("Signed in to another server: deleting the downloads kept from the last one")
        signedOut(null)
    }

    /**
     * The server refused this device's sign-in (signed out from the web's Sign-ins list, or a new key). The downloads
     * stay, with what was watched offline and not yet sent, for when the device signs in to [address] again; transfers
     * stop until then. True when there was anything to keep.
     */
    fun signedOutByServer(address: String): Boolean {
        work.cancelAllWorkByTag(WORK_TAG)
        UserInitiatedTransfers.cancelAll(context)
        transferring.clear()
        client = null
        if (all.isEmpty()) return false
        prefs.edit { putString(KEPT_FOR, address) }
        flexarrLog.info("Signed out by the server: keeping ${all.size} downloads for when it's signed in again")
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
        DownloadRules.finishedWith(all, System.currentTimeMillis()).forEach { remove(it.id, tellServer = true) }
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
        for (download in all.filter { it.profileId == profileId && it.unsent != null }) {
            val unsent = download.unsent ?: continue
            try {
                client.reportOfflineProgress(download.item.id, unsent.position, unsent.finished, Instant.ofEpochMilli(unsent.playedAtMillis))
                update(download.id) { if (it.unsent == unsent) it.copy(unsent = null) else it }
                model.watchStateChanged()
            } catch (e: FlexarrException.Http) {
                // The title's gone from the server: there's nothing left to tell it about.
                if (e.status == 404) update(download.id) { if (it.unsent == unsent) it.copy(unsent = null) else it }
                else flexarrLog.warning("Couldn't send offline progress", e)
            } catch (e: FlexarrException) {
                if (e !is FlexarrException.Unreachable) flexarrLog.warning("Couldn't send offline progress", e)
            }
        }
        // A title saved by an app that didn't know its kind (a podcast episode, kept by an older version) was saved as
        // Unknown and would stay so, filed under Films: what the server says now puts it right, as on iPhone.
        for (download in all.filter { it.profileId == profileId && it.item.kind == MediaKind.Unknown }) {
            val remote = runCatching { client.offlineDownload(download.id) }.getOrNull() ?: continue
            if (remote.item.kind != MediaKind.Unknown) update(download.id) { it.copy(item = remote.item) }
        }
        // A transfer for everything still to come (one that's running already is left alone).
        all.filter { it.isPending }.forEach { enqueue(it, replace = false) }
        downloads.forEach { fetchNext(after = it) }
        reconcilePodcasts()
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
        val network = if (DownloadNetwork.needsUnmetered(isWiFiOnly(download), download.automatic, dataSaver)) NetworkType.UNMETERED else NetworkType.CONNECTED
        val request = OneTimeWorkRequestBuilder<DownloadWorker>()
            .setInputData(workDataOf(DownloadWorker.ID to download.id))
            .setConstraints(Constraints.Builder().setRequiredNetworkType(network).setRequiresStorageNotLow(true).build())
            .addTag(WORK_TAG)
            .build()
        work.enqueueUniqueWork(workName(download.id), if (replace) ExistingWorkPolicy.REPLACE else ExistingWorkPolicy.KEEP, request)
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
        val client = currentClient ?: return
        scope.launch { runCatching { client.markDownloadReceived(id) } }
        byId(id)?.let(::fetchNext)
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
     * aren't wanted any more (played, pushed out by newer ones) go, unless they're part-heard.
     */
    suspend fun reconcilePodcasts() {
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
        download.fileName?.let { fileIn(folder, it)?.delete() }
        fileIn(folder, "${safeName(id)}.part")?.delete()
        all = all.filterNot { it.id == id }
        save()
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
        save()
        saveCollections()
        profileId = null
        unsentDeletes = emptySet()
        prefs.edit { remove(PROFILE).remove(KEPT_FOR) }
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

    private fun <T> write(list: List<T>, serializer: KSerializer<T>, name: String) {
        runCatching {
            val text = FlexarrClient.json.encodeToString(ListSerializer(serializer), list)
            val temporary = File(folder, "$name.tmp")
            temporary.writeText(text)
            temporary.renameTo(File(folder, name))
        }.onFailure { flexarrLog.error("Couldn't save $name", it) }
    }

    private val folder: File get() = folder(context)

    companion object {
        private const val TAG = "Flexarr"
        private const val MANIFEST = "downloads.json"
        private const val COLLECTIONS = "collections.json"
        private const val PROFILE = "profile"
        private const val QUALITY = "quality"
        private const val NEXT = "next"
        private const val WIFI_ONLY = "wifiOnly"
        private const val DECLINED = "declinedEpisodes"
        private const val UNSENT_DELETES = "unsentDeletes"
        /** The server whose downloads were kept when it refused this device's sign-in. */
        private const val KEPT_FOR = "keptFor"
        private const val WORK_TAG = "flexarr-download"
        private const val SAVE_GAP_MS = 250L
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
