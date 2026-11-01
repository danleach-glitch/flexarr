package io.github.flexarr.downloads

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.edit
import androidx.work.Constraints
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.workDataOf
import io.github.flexarr.AppModel
import io.github.flexarr.core.BookDownloadRules
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.ItemSummary
import io.github.flexarr.core.LocalBook
import io.github.flexarr.core.LocalPhase
import io.github.flexarr.core.SettledSender
import io.github.flexarr.core.bookManifest
import io.github.flexarr.core.recordBookProgress
import io.github.flexarr.logging.FlexarrLog
import io.github.flexarr.player.DeviceCapabilities
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.serialization.builtins.ListSerializer
import okhttp3.Request
import java.io.File

private val flexarrLog = FlexarrLog("downloads")

/**
 * Audiobooks on this phone, each a whole book under one entry: the item's manifest says which files make it up (as they
 * are, or converted to AAC where the phone can't decode one), and [BookDownloadWorker] fetches them in turn with
 * WorkManager, carrying on where a transfer stopped. What's heard offline is kept with the book and sent with the time
 * it was heard. Optionally the next book in a series comes by itself once one's finished, and finished books go.
 */
class BookDownloads(private val context: Context, private val model: AppModel) {
    private val prefs = context.getSharedPreferences("book-downloads", Context.MODE_PRIVATE)
    private val work = WorkManager.getInstance(context)
    private val scope = model.scope

    /** Every book on the phone, whoever's it is. */
    var all: List<LocalBook> by mutableStateOf(emptyList())
        private set

    /** How far each book's transfer has got, kept apart from [all] so the lists don't redraw at every step. */
    private val transferring = mutableStateMapOf<String, Double>()

    /** Fetch the next book in a series by itself once one's finished. */
    var downloadsNextInSeries: Boolean by mutableStateOf(prefs.getBoolean(NEXT, true))
        private set

    /** Delete a downloaded book once it's finished. */
    var removesFinished: Boolean by mutableStateOf(prefs.getBoolean(REMOVE_FINISHED, false))
        private set

    private var loaded = false
    private val saves = Channel<List<LocalBook>>(Channel.CONFLATED)

    private val loading: Job = scope.launch {
        val saved = withContext(Dispatchers.IO) {
            folder.mkdirs()
            DownloadStore.load(File(folder, MANIFEST), LocalBook.serializer())
        }
        all = saved
        loaded = true
    }

    init {
        scope.launch(Dispatchers.IO) {
            for (list in saves) {
                runCatching {
                    val temporary = File(folder, "$MANIFEST.tmp")
                    temporary.writeText(FlexarrClient.json.encodeToString(ListSerializer(LocalBook.serializer()), list))
                    temporary.renameTo(File(folder, MANIFEST))
                }.onFailure { flexarrLog.error("Couldn't save the downloaded books", it) }
                delay(250)
            }
        }
    }

    suspend fun ready() = loading.join()

    private val profileId: String? get() = model.downloads.profileId ?: model.currentProfile?.id

    /** This profile's books, newest first. */
    val books: List<LocalBook> get() = all.filter { it.profileId == profileId }.sortedByDescending { it.createdAtMillis }

    fun entry(itemId: String): LocalBook? = all.firstOrNull { it.profileId == profileId && it.itemId == itemId }

    fun isDownloaded(itemId: String): Boolean = entry(itemId)?.isDownloaded == true

    val isTransferring: Boolean get() = transferring.isNotEmpty()

    fun progress(book: LocalBook): Double = transferring[book.itemId] ?: book.progress

    val bytesUsed: Long get() = all.sumOf { book -> book.fileNames.filterNotNull().sumOf { name -> fileIn(name)?.length() ?: 0L } }

    fun file(name: String?): File? = name?.let(::fileIn)?.takeIf { it.exists() }

    fun image(book: LocalBook): File? = file(book.imageFileName)

    /** The book's files on the phone in order, once they're all here. */
    fun files(book: LocalBook): List<File>? = book.fileNames.map { file(it) ?: return null }

    // Settings

    fun changeDownloadsNextInSeries(on: Boolean) {
        downloadsNextInSeries = on
        prefs.edit { putBoolean(NEXT, on) }
    }

    fun changeRemovesFinished(on: Boolean) {
        removesFinished = on
        prefs.edit { putBoolean(REMOVE_FINISHED, on) }
        if (on) scope.launch { catchUp() }
    }

    // Downloading

    /** Downloads a whole book. Here already or on its way: nothing to do (a failed one starts again). */
    suspend fun download(item: ItemSummary, automatic: Boolean = false) {
        ready()
        val client = model.client ?: throw FlexarrException.Unauthorized()
        val profileId = profileId ?: throw FlexarrException.Unauthorized()
        entry(item.id)?.let { existing ->
            if (existing.phase != LocalPhase.Failed) return
            remove(existing.itemId)
        }
        val manifest = client.bookManifest(item.id, codecs)
        val book = LocalBook(
            itemId = item.id,
            profileId = profileId,
            item = item,
            manifest = manifest,
            fileNames = List(manifest.files.size) { null },
            position = manifest.progress.positionSeconds,
            automatic = automatic,
            createdAtMillis = System.currentTimeMillis(),
        )
        all = all.filterNot { it.profileId == profileId && it.itemId == item.id } + book
        save()
        flexarrLog.info("Downloading the book ${item.title}: ${manifest.files.size} files")
        enqueue(book)
        fetchImage(book, client)?.let { name -> update(book.itemId) { it.copy(imageFileName = name) } }
    }

    private fun enqueue(book: LocalBook) {
        val wifiOnly = book.automatic && model.downloads.automaticOnWiFiOnly
        val request = OneTimeWorkRequestBuilder<BookDownloadWorker>()
            .setInputData(workDataOf(BookDownloadWorker.ITEM to book.itemId, BookDownloadWorker.PROFILE to book.profileId))
            .setConstraints(Constraints.Builder().setRequiredNetworkType(if (wifiOnly) NetworkType.UNMETERED else NetworkType.CONNECTED).setRequiresStorageNotLow(true).build())
            .addTag(WORK_TAG)
            .build()
        work.enqueueUniqueWork(workName(book), ExistingWorkPolicy.KEEP, request)
    }

    private suspend fun fetchImage(book: LocalBook, client: FlexarrClient): String? {
        val url = client.imageUrl(book.item.artwork.poster, 600) ?: return null
        val name = BookDownloadRules.imageName(book.itemId)
        return withContext(Dispatchers.IO) {
            runCatching {
                FlexarrClient.sharedHttp.newCall(Request.Builder().url(url).build()).execute().use { response ->
                    if (!response.isSuccessful) return@runCatching null
                    fileIn(name)!!.outputStream().use { response.body.byteStream().copyTo(it) }
                    name
                }
            }.getOrNull()
        }
    }

    // The transfer's reports (on the main thread)

    internal fun byKey(itemId: String, profileId: String): LocalBook? = all.firstOrNull { it.itemId == itemId && it.profileId == profileId }

    internal fun progressed(itemId: String, fraction: Double) {
        transferring[itemId] = fraction
    }

    internal fun fileArrived(itemId: String, profileId: String, index: Int, name: String) {
        updateFor(itemId, profileId) { book -> book.copy(fileNames = book.fileNames.toMutableList().also { if (index < it.size) it[index] = name }) }
    }

    internal fun arrived(itemId: String, profileId: String) {
        transferring.remove(itemId)
        updateFor(itemId, profileId) { book ->
            val size = book.fileNames.filterNotNull().sumOf { fileIn(it)?.length() ?: 0L }
            book.copy(phase = LocalPhase.Downloaded, progress = 1.0, failure = null, sizeBytes = size)
        }
        flexarrLog.info("Downloaded the book $itemId")
    }

    internal fun failed(itemId: String, profileId: String, message: String) {
        transferring.remove(itemId)
        updateFor(itemId, profileId) { it.copy(phase = LocalPhase.Failed, failure = message) }
    }

    // Listening offline

    /** Heard to [position] (or the end) from the phone's copy: kept here, and sent when the server can be reached. */
    fun record(itemId: String, position: Double, finished: Boolean) {
        entry(itemId) ?: return
        update(itemId) { it.recorded(position, finished, System.currentTimeMillis()) }
        if (finished || System.currentTimeMillis() - lastSent > SEND_EVERY_MS) scope.launch { catchUp() }
    }

    /** A speed chosen while the book plays from here: kept with it, and sent to the server now or when it can be. */
    fun speedChosen(itemId: String, speed: Double) {
        entry(itemId) ?: return
        update(itemId) { it.speedChosen(speed) }
        speeds.changed(itemId)
    }

    /** One catch-up at a time, and a speed being sent in between, so saves land in the order they were made. */
    private val sending = Mutex()

    /** Speed taps (0.05 at a time) sent once they settle: only the speed, the latest. */
    private val speeds = SettledSender<String>(scope, sending) { itemId -> sendSpeed(itemId) }

    private suspend fun sendSpeed(itemId: String) {
        val client = model.client ?: return
        if (!model.downloads.isOnline) return
        val speed = entry(itemId)?.unsentSpeed ?: return
        try {
            client.setPlaybackSpeed(itemId, speed)
            update(itemId) { if (it.unsentSpeed == speed) it.copy(unsentSpeed = null) else it }
        } catch (e: FlexarrException.Http) {
            if (e.status == 404) update(itemId) { if (it.unsentSpeed == speed) it.copy(unsentSpeed = null) else it }
        } catch (_: FlexarrException) {
            // Out of reach: the next catch-up sends it.
        }
    }

    /** The server's newer manifest for a book here: its bookmarks, listening settings and speed are kept with it. */
    fun refreshed(itemId: String, fresh: io.github.flexarr.core.BookManifest): LocalBook? {
        entry(itemId) ?: return null
        update(itemId) { it.refreshed(fresh) }
        return entry(itemId)
    }

    private var lastSent = 0L

    /**
     * Sends what was heard offline (with the time it was heard), deletes finished books when Settings says so, fetches the
     * next in a series that's due, and gives anything still to come a transfer.
     */
    suspend fun catchUp() = sending.withLock { catchUpNow() }

    private suspend fun catchUpNow() {
        ready()
        val client = model.client
        if (client != null && model.downloads.isOnline) {
            lastSent = System.currentTimeMillis()
            for (book in all.filter { it.profileId == profileId && it.unsent != null }) {
                val unsent = book.unsent ?: continue
                try {
                    client.recordBookProgress(book.itemId, unsent.position, model.deviceName, unsent.playedAtMillis, ended = unsent.finished)
                    update(book.itemId) { if (it.unsent == unsent) it.copy(unsent = null) else it }
                    model.watchStateChanged()
                } catch (e: FlexarrException.Http) {
                    if (e.status == 404) update(book.itemId) { if (it.unsent == unsent) it.copy(unsent = null) else it }
                } catch (_: FlexarrException) {
                    break // out of reach
                }
            }
            // Speeds chosen offline: the book's own speed on the server.
            for (book in all.filter { it.profileId == profileId && it.unsentSpeed != null }) {
                val speed = book.unsentSpeed ?: continue
                try {
                    client.setPlaybackSpeed(book.itemId, speed)
                    update(book.itemId) { if (it.unsentSpeed == speed) it.copy(unsentSpeed = null) else it }
                } catch (e: FlexarrException.Http) {
                    if (e.status == 404) update(book.itemId) { if (it.unsentSpeed == speed) it.copy(unsentSpeed = null) else it }
                } catch (_: FlexarrException) {
                    break // out of reach
                }
            }
            if (model.downloads.isOnline) {
                books.mapNotNull { BookDownloadRules.nextToFetch(it, all, downloadsNextInSeries) }.distinctBy { it.id }.forEach { next ->
                    runCatching { download(next, automatic = true) }.onFailure { flexarrLog.warning("Couldn't fetch the next book by itself", it) }
                }
            }
        }
        BookDownloadRules.finishedWith(books, removesFinished, model.books::isPlayingFile).forEach { remove(it.itemId) }
        books.filter { it.isPending }.forEach(::enqueue)
    }

    // Deleting

    fun remove(itemId: String) {
        val book = entry(itemId) ?: return
        work.cancelUniqueWork(workName(book))
        transferring.remove(itemId)
        deleteFiles(book)
        all = all.filterNot { it.itemId == book.itemId && it.profileId == book.profileId }
        save()
    }

    private fun deleteFiles(book: LocalBook) {
        book.fileNames.filterNotNull().forEach { fileIn(it)?.delete() }
        book.manifest.files.indices.forEach { fileIn(BookDownloadRules.fileName(book.itemId, it) + ".part")?.delete() }
        book.imageFileName?.let(::fileIn)?.delete()
    }

    /** Signing out, or Delete all: what was heard offline is sent first while it can be, then every book goes. */
    fun deleteAll(client: FlexarrClient? = model.client) {
        val pending = all.mapNotNull { book -> book.unsent?.let { book.itemId to it } }
        work.cancelAllWorkByTag(WORK_TAG)
        transferring.clear()
        all.forEach(::deleteFiles)
        all = emptyList()
        save()
        if (client != null && pending.isNotEmpty()) {
            val device = model.deviceName
            scope.launch {
                pending.forEach { (itemId, unsent) -> runCatching { client.recordBookProgress(itemId, unsent.position, device, unsent.playedAtMillis, unsent.finished) } }
            }
        }
    }

    // Saving

    private fun update(itemId: String, change: (LocalBook) -> LocalBook) {
        val profile = profileId ?: return
        updateFor(itemId, profile, change)
    }

    private fun updateFor(itemId: String, profileId: String, change: (LocalBook) -> LocalBook) {
        if (all.none { it.itemId == itemId && it.profileId == profileId }) return
        all = all.map { if (it.itemId == itemId && it.profileId == profileId) change(it) else it }
        save()
    }

    private fun save() {
        if (loaded) saves.trySend(all)
    }

    internal val folder: File get() = folder(context)

    internal fun fileIn(name: String): File? = DownloadStore.fileIn(folder, name)

    /** The sounds this phone decodes: files in any other come converted to AAC. */
    val codecs: List<String> get() = DeviceCapabilities.profile(context).audioCodecs

    companion object {
        private const val MANIFEST = "books.json"
        private const val NEXT = "nextInSeries"
        private const val REMOVE_FINISHED = "removeFinished"
        private const val WORK_TAG = "flexarr-book-download"
        private const val SEND_EVERY_MS = 60_000L

        /** Apart from the other downloads' folder, whose clean-up deletes what it doesn't know. */
        fun folder(context: Context): File = File(context.noBackupFilesDir, "books")

        fun workName(book: LocalBook) = "book-download-${book.profileId}-${book.itemId}"
    }
}
