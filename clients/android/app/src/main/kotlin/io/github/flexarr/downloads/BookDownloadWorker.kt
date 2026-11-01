package io.github.flexarr.downloads

import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkerParameters
import io.github.flexarr.appModel
import io.github.flexarr.core.BookDownloadRules
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.LocalBook
import io.github.flexarr.core.bookFileRequest
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import java.io.FileOutputStream
import java.io.IOException

private val flexarrLog = FlexarrLog("downloads")

/**
 * A downloaded book's files, fetched one after another from the item's own addresses (signed in), each into a `.part`
 * file that a stopped transfer carries on from. Shows as a notification while it runs, as the other downloads do.
 */
class BookDownloadWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    private val store get() = applicationContext.appModel.bookDownloads

    override suspend fun doWork(): Result {
        val itemId = inputData.getString(ITEM) ?: return Result.failure()
        val profileId = inputData.getString(PROFILE) ?: return Result.failure()
        store.ready()
        val book = onMain { store.byKey(itemId, profileId) } ?: return Result.success()
        if (book.isDownloaded) return Result.success()
        val client = onMain { applicationContext.appModel.client } ?: return Result.retry()
        runCatching { setForeground(foregroundInfo(book, null)) }
        return try {
            val total = book.manifest.files.sumOf { it.size }.coerceAtLeast(1)
            var done = 0L
            for (file in book.manifest.files) {
                val name = BookDownloadRules.fileName(itemId, file.index)
                val here = onMain { store.byKey(itemId, profileId) }?.fileNames?.getOrNull(file.index)
                if (here != null && store.file(here) != null) {
                    done += file.size
                    continue
                }
                fetch(client, book, file.url, name) { written ->
                    val fraction = ((done + written).toDouble() / total).coerceIn(0.0, 0.99)
                    onMain { store.progressed(itemId, fraction) }
                    runCatching { setForeground(foregroundInfo(book, fraction)) }
                }
                onMain { store.fileArrived(itemId, profileId, file.index, name) }
                done += file.size
            }
            onMain { store.arrived(itemId, profileId) }
            Result.success()
        } catch (e: CancellationException) {
            throw e
        } catch (e: FlexarrException.Unauthorized) {
            onMain { store.failed(itemId, profileId, e.message ?: "The server didn’t accept this device’s sign-in.") }
            Result.success()
        } catch (e: Exception) {
            flexarrLog.warning("The book download $itemId stopped; trying again later", e)
            if (runAttemptCount >= DownloadTransfer.MAX_ATTEMPTS) {
                onMain { store.failed(itemId, profileId, "The download stopped. Try again.") }
                Result.success()
            } else {
                Result.retry()
            }
        }
    }

    /** One file into `<name>.part`, carrying on from its end when the server allows, then given its name. */
    private suspend fun fetch(client: FlexarrClient, book: LocalBook, url: String, name: String, progressed: suspend (Long) -> Unit) = withContext(Dispatchers.IO) {
        val part = store.fileIn("$name.part") ?: throw IOException("Not a download's name")
        val already = if (part.exists()) part.length() else 0L
        val request = client.bookFileRequest(url, already) ?: throw IOException("Not the server's address")
        FlexarrClient.sharedHttp.newCall(request).execute().use { response ->
            if (response.code == 401) throw FlexarrException.Unauthorized()
            if (response.code == 416 && already > 0) {
                // All of it came last time: it only wasn't named.
                return@use
            }
            if (response.code != 200 && response.code != 206) throw IOException("The server answered ${response.code} for ${book.item.title}")
            val resuming = response.code == 206
            var written = if (resuming) already else 0L
            var lastShown = 0L
            FileOutputStream(part, resuming).use { out ->
                response.body.byteStream().use { input ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        coroutineContext.ensureActive()
                        val read = input.read(buffer)
                        if (read < 0) break
                        out.write(buffer, 0, read)
                        written += read
                        val now = System.currentTimeMillis()
                        if (now - lastShown > 500) {
                            lastShown = now
                            progressed(written)
                        }
                    }
                }
            }
        }
        val destination = store.fileIn(name) ?: throw IOException("Not a download's name")
        destination.delete()
        if (!part.renameTo(destination)) throw IOException("Couldn't keep the downloaded file")
    }

    override suspend fun getForegroundInfo(): ForegroundInfo {
        val book = inputData.getString(ITEM)?.let { id -> inputData.getString(PROFILE)?.let { profile -> onMain { store.byKey(id, profile) } } }
        return foregroundInfo(book, null)
    }

    private fun foregroundInfo(book: LocalBook?, fraction: Double?): ForegroundInfo {
        val manager = applicationContext.getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL) == null) {
            manager.createNotificationChannel(NotificationChannel(CHANNEL, "Downloads", NotificationManager.IMPORTANCE_LOW))
        }
        val notification = NotificationCompat.Builder(applicationContext, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle(book?.item?.title ?: "Downloading")
            .setContentText("Downloading")
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .apply { if (fraction != null) setProgress(1000, (fraction * 1000).toInt(), false) else setProgress(0, 0, true) }
            .build()
        val id = ("book-" + (book?.itemId ?: "")).hashCode()
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) ForegroundInfo(id, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC) else ForegroundInfo(id, notification)
    }

    companion object {
        const val ITEM = "item"
        const val PROFILE = "profile"
        /** The same channel as the other downloads. */
        private const val CHANNEL = "downloads"
    }
}
