package io.github.flexarr.downloads

import io.github.flexarr.logging.FlexarrLog
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
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.LocalDownload
import io.github.flexarr.core.LocalPhase
import io.github.flexarr.core.downloadFileRequest
import io.github.flexarr.core.offlineDownload
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileOutputStream
import java.io.IOException

private val flexarrLog = FlexarrLog("downloads")

/**
 * One download's transfer, run by WorkManager so it carries on with the app closed and starts again after a restart:
 * follows the server getting the file ready, then fetches it, carrying on from where an interrupted transfer got to.
 * It shows as a notification while it runs (Android's rule for long work), and waits for Wi-Fi when it must.
 */
class DownloadWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    private val store get() = applicationContext.appModel.downloads

    override suspend fun doWork(): Result {
        val id = inputData.getString(ID) ?: return Result.failure()
        val local = onMain { store.byId(id) } ?: return Result.success() // deleted meanwhile
        if (local.isDownloaded) return Result.success()
        val client = onMain { store.currentClient } ?: return Result.retry()
        runCatching { setForeground(foregroundInfo(local, "Getting ready")) }
        return try {
            // The server converts it first: asked after every few seconds until it's ready.
            while (true) {
                val remote = try {
                    client.offlineDownload(id)
                } catch (e: FlexarrException.Http) {
                    if (e.status == 404) {
                        onMain { store.failed(id, "The server doesn’t have this download any more. Download it again.") }
                        return Result.success()
                    }
                    throw e
                }
                if (onMain { store.apply(remote) }) break
                if (onMain { store.byId(id)?.phase } != LocalPhase.Preparing) return Result.success() // failed, forgotten or deleted
                delay(POLL_MS)
            }
            val ready = onMain { store.byId(id) } ?: return Result.success()
            runCatching { setForeground(foregroundInfo(ready, "Downloading")) }
            fetch(client, ready)
            Result.success()
        } catch (e: CancellationException) {
            throw e // stopped: deleted, or off Wi-Fi (what came so far is kept for next time)
        } catch (e: FlexarrException.Unauthorized) {
            onMain { store.failed(id, e.message ?: "The server didn’t accept this device’s sign-in.") }
            Result.success()
        } catch (e: NoRoom) {
            onMain { store.failed(id, "There isn’t room for this on the phone. Delete something and try again.") }
            Result.success()
        } catch (e: Exception) {
            // Out of reach, or the connection dropped: tried again later, from where it got to.
            flexarrLog.warning("Download $id stopped; trying again later", e)
            if (runAttemptCount >= MAX_ATTEMPTS) {
                onMain { store.failed(id, "The download stopped. Try again.") }
                Result.success()
            } else {
                Result.retry()
            }
        }
    }

    /** Fetches the file into `<id>.part`, carrying on from its end when the server allows, then gives it its name. */
    private suspend fun fetch(client: FlexarrClient, download: LocalDownload) = withContext(Dispatchers.IO) {
        val folder = DownloadStore.folder(applicationContext)
        val part = File(folder, "${download.id}.part")
        val already = if (part.exists()) part.length() else 0L
        val response = client.downloadFileRequest(download.id, already).let { FlexarrClient.sharedHttp.newCall(it).execute() }
        response.use {
            if (it.code == 401) throw FlexarrException.Unauthorized()
            if (it.code != 200 && it.code != 206) throw IOException("The server answered ${it.code} for the file")
            // 206 carries on from where it got to; 200 is the whole file again.
            val resuming = it.code == 206
            val start = if (resuming) already else 0L
            val total = it.body.contentLength().takeIf { length -> length > 0 }?.let { length -> length + start }
            if (total != null && folder.usableSpace < total - start + RESERVE_BYTES) throw NoRoom()
            var written = start
            var lastShown = 0L
            FileOutputStream(part, resuming).use { out ->
                it.body.byteStream().use { input ->
                    val buffer = ByteArray(64 * 1024)
                    while (true) {
                        coroutineContext.ensureActive()
                        val read = input.read(buffer)
                        if (read < 0) break
                        out.write(buffer, 0, read)
                        written += read
                        val now = System.currentTimeMillis()
                        if (total != null && now - lastShown > PROGRESS_MS) {
                            lastShown = now
                            val fraction = written.toDouble() / total
                            onMain { store.progressed(download.id, fraction) }
                            runCatching { setForeground(foregroundInfo(download, "Downloading", fraction)) }
                        }
                    }
                }
            }
            if (total != null && written < total) throw IOException("The file stopped short ($written of $total bytes)")
        }
        val name = "${download.id}.${download.fileExtension}"
        val destination = File(folder, name)
        destination.delete()
        if (!part.renameTo(destination)) throw IOException("Couldn't keep the downloaded file")
        onMain { store.arrived(download.id, name, destination.length()) }
    }

    override suspend fun getForegroundInfo(): ForegroundInfo {
        val local = inputData.getString(ID)?.let { id -> onMain { store.byId(id) } }
        return foregroundInfo(local, "Downloading")
    }

    private fun foregroundInfo(download: LocalDownload?, status: String, fraction: Double? = null): ForegroundInfo {
        val manager = applicationContext.getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL) == null) {
            manager.createNotificationChannel(NotificationChannel(CHANNEL, "Downloads", NotificationManager.IMPORTANCE_LOW))
        }
        val title = download?.item?.let { item -> if (item.seriesTitle != null && item.kind != io.github.flexarr.core.MediaKind.Movie) "${item.seriesTitle}: ${item.title}" else item.title }
        val notification = NotificationCompat.Builder(applicationContext, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle(title ?: "Downloading")
            .setContentText(status)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .apply { if (fraction != null) setProgress(1000, (fraction * 1000).toInt(), false) else setProgress(0, 0, true) }
            .build()
        val notificationId = (download?.id ?: "download").hashCode()
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ForegroundInfo(notificationId, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            ForegroundInfo(notificationId, notification)
        }
    }

    private class NoRoom : IOException("No room on the phone")

    companion object {
        const val ID = "id"
        private const val CHANNEL = "downloads"
        private const val POLL_MS = 3_000L
        private const val PROGRESS_MS = 500L
        private const val MAX_ATTEMPTS = 20
        /** Left free beside a download, so the phone isn't filled to the brim. */
        private const val RESERVE_BYTES = 200L * 1024 * 1024
    }
}

/** The store's state lives on the main thread. */
private suspend fun <T> onMain(block: () -> T): T = withContext(Dispatchers.Main) { block() }
