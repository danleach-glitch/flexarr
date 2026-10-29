package io.github.flexarr.downloads

import io.github.flexarr.logging.FlexarrLog
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import androidx.core.app.NotificationCompat
import io.github.flexarr.appModel
import io.github.flexarr.core.FlexarrClient
import io.github.flexarr.core.FlexarrException
import io.github.flexarr.core.LocalDownload
import io.github.flexarr.core.LocalPhase
import io.github.flexarr.core.MediaKind
import io.github.flexarr.core.downloadFileRequest
import io.github.flexarr.core.offlineDownload
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import java.io.FileOutputStream
import java.io.IOException

private val flexarrLog = FlexarrLog("downloads")

/**
 * One download's transfer, the same whichever runs it ([DownloadWorker] through WorkManager, or [DownloadJobService] as
 * a user-initiated data transfer job): follows the server getting the file ready, then fetches it, carrying on from
 * where an interrupted transfer got to. [show] puts what it's doing in the notification Android asks for while it runs;
 * [transferred] tells the system how much has come of how much (for a user-initiated job).
 */
internal class DownloadTransfer(
    private val context: Context,
    private val show: suspend (download: LocalDownload, status: String, fraction: Double?) -> Unit,
    private val transferred: (written: Long, total: Long?) -> Unit = { _, _ -> },
) {
    enum class Outcome {
        /** Here, failed for good, or deleted meanwhile: nothing more to do. */
        Done,
        /** Out of reach, or the connection dropped: to be tried again later, from where it got to. */
        Retry,
    }

    private val store get() = context.appModel.downloads

    /** Runs the transfer of download [id]; [attempt] is how many times it's been tried before (it gives up after a while). */
    suspend fun run(id: String, attempt: Int): Outcome {
        store.ready()
        val local = onMain { store.byId(id) } ?: return Outcome.Done // deleted meanwhile
        if (local.isDownloaded) return Outcome.Done
        // Started twice (a job and a worker, after a change of plan): the one already running carries on alone.
        if (!onMain { running.add(id) }) return Outcome.Done
        try {
            val client = onMain { store.currentClient } ?: return Outcome.Retry
            show(local, "Getting ready", null)
            // The server converts it first: asked after every few seconds until it's ready.
            while (true) {
                val remote = try {
                    client.offlineDownload(id)
                } catch (e: FlexarrException.Http) {
                    if (e.status == 404) {
                        onMain { store.failed(id, "The server doesn’t have this download any more. Download it again.") }
                        return Outcome.Done
                    }
                    throw e
                }
                if (onMain { store.apply(remote) }) break
                if (onMain { store.byId(id)?.phase } != LocalPhase.Preparing) return Outcome.Done // failed, forgotten or deleted
                delay(POLL_MS)
            }
            val ready = onMain { store.byId(id) } ?: return Outcome.Done
            show(ready, "Downloading", null)
            fetch(client, ready)
            return Outcome.Done
        } catch (e: CancellationException) {
            throw e // stopped: deleted, or off Wi-Fi (what came so far is kept for next time)
        } catch (e: FlexarrException.Unauthorized) {
            onMain { store.failed(id, e.message ?: "The server didn’t accept this device’s sign-in.") }
            return Outcome.Done
        } catch (e: NoRoom) {
            onMain { store.failed(id, "There isn’t room for this on the phone. Delete something and try again.") }
            return Outcome.Done
        } catch (e: Exception) {
            flexarrLog.warning("Download $id stopped; trying again later", e)
            if (attempt >= MAX_ATTEMPTS) {
                onMain { store.failed(id, "The download stopped. Try again.") }
                return Outcome.Done
            }
            return Outcome.Retry
        } finally {
            withContext(NonCancellable) { onMain { running.remove(id) } }
        }
    }

    /** Fetches the file into `<id>.part`, carrying on from its end when the server allows, then gives it its name. */
    private suspend fun fetch(client: FlexarrClient, download: LocalDownload) = withContext(Dispatchers.IO) {
        val folder = DownloadStore.folder(context)
        // Named from the server's id and extension, kept to plain characters so neither can reach outside the folder.
        val part = DownloadStore.fileIn(folder, "${DownloadStore.safeName(download.id)}.part") ?: throw IOException("Not a download's name")
        val already = if (part.exists()) part.length() else 0L
        val response = client.downloadFileRequest(download.id, already).let { FlexarrClient.sharedHttp.newCall(it).execute() }
        response.use {
            if (it.code == 401) throw FlexarrException.Unauthorized()
            if (it.code == 416) {
                // Asked to carry on from the end: the whole file came last time, and the app stopped before naming it.
                if (isComplete(already, it.header("Content-Range"), download.sizeBytes)) return@use
                part.delete()
                throw IOException("The server couldn't carry on from byte $already; starting again")
            }
            if (it.code != 200 && it.code != 206) throw IOException("The server answered ${it.code} for the file")
            // 206 carries on from where it got to; 200 is the whole file again.
            val resuming = it.code == 206
            val start = if (resuming) already else 0L
            val total = it.body.contentLength().takeIf { length -> length > 0 }?.let { length -> length + start }
            if (total != null && folder.usableSpace < total - start + RESERVE_BYTES) throw NoRoom()
            var written = start
            var lastShown = 0L
            transferred(written, total)
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
                            transferred(written, total)
                            show(download, "Downloading", fraction)
                        }
                    }
                }
            }
            if (total != null && written < total) throw IOException("The file stopped short ($written of $total bytes)")
        }
        val name = "${DownloadStore.safeName(download.id)}.${DownloadStore.safeName(download.fileExtension)}"
        val destination = DownloadStore.fileIn(folder, name) ?: throw IOException("Not a download's name")
        destination.delete()
        if (!part.renameTo(destination)) throw IOException("Couldn't keep the downloaded file")
        onMain { store.arrived(download.id, name, destination.length()) }
    }

    private class NoRoom : IOException("No room on the phone")

    companion object {
        /** Downloads whose transfer is running in this process (read and changed on the main thread). */
        private val running = mutableSetOf<String>()

        /**
         * A 416 when asking for what follows [partBytes]: the file is all there if the server's Content-Range
         * (the total after its slash) or, failing that, the size it gave earlier, is just that long.
         */
        internal fun isComplete(partBytes: Long, contentRange: String?, expectedBytes: Long?): Boolean {
            if (partBytes <= 0) return false
            val total = contentRange?.substringAfterLast('/', "")?.trim()?.toLongOrNull() ?: expectedBytes
            return total == partBytes
        }

        /** The notification a transfer shows while it runs: its title, what it's doing, and how far it's got. */
        fun notification(context: Context, download: LocalDownload?, status: String, fraction: Double? = null): Notification {
            val manager = context.getSystemService(NotificationManager::class.java)
            if (manager.getNotificationChannel(CHANNEL) == null) {
                manager.createNotificationChannel(NotificationChannel(CHANNEL, "Downloads", NotificationManager.IMPORTANCE_LOW))
            }
            val title = download?.item?.let { item -> if (item.seriesTitle != null && item.kind != MediaKind.Movie) "${item.seriesTitle}: ${item.title}" else item.title }
            return NotificationCompat.Builder(context, CHANNEL)
                .setSmallIcon(android.R.drawable.stat_sys_download)
                .setContentTitle(title ?: "Downloading")
                .setContentText(status)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setSilent(true)
                .apply { if (fraction != null) setProgress(1000, (fraction * 1000).toInt(), false) else setProgress(0, 0, true) }
                .build()
        }

        /** One notification per download, whichever runs its transfer. */
        fun notificationId(id: String?): Int = (id ?: "download").hashCode()

        private const val CHANNEL = "downloads"
        private const val POLL_MS = 3_000L
        private const val PROGRESS_MS = 500L
        const val MAX_ATTEMPTS = 20
        /** Left free beside a download, so the phone isn't filled to the brim. */
        private const val RESERVE_BYTES = 200L * 1024 * 1024
    }
}

/** The store's state lives on the main thread. */
internal suspend fun <T> onMain(block: () -> T): T = withContext(Dispatchers.Main) { block() }
