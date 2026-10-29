package io.github.flexarr.downloads

import io.github.flexarr.logging.FlexarrLog
import android.content.Context
import android.content.pm.ServiceInfo
import android.os.Build
import androidx.work.CoroutineWorker
import androidx.work.ForegroundInfo
import androidx.work.WorkerParameters
import io.github.flexarr.appModel
import io.github.flexarr.core.LocalDownload
import kotlinx.coroutines.CancellationException

private val flexarrLog = FlexarrLog("downloads")

/**
 * One download's transfer ([DownloadTransfer]), run by WorkManager so it carries on with the app closed and starts again
 * after a restart. It shows as a notification while it runs (Android's rule for long work), and waits for Wi-Fi when it
 * must. Downloads asked for by hand on Android 14 on run as user-initiated jobs instead ([DownloadJobService]); these
 * are the ones the phone fetches by itself, those on older Android, and any Android wouldn't take as a job.
 */
class DownloadWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    private val store get() = applicationContext.appModel.downloads

    override suspend fun doWork(): Result {
        val id = inputData.getString(ID) ?: return Result.failure()
        val transfer = DownloadTransfer(applicationContext, show = { download, status, fraction -> promote(foregroundInfo(download, status, fraction)) })
        return when (transfer.run(id, runAttemptCount)) {
            DownloadTransfer.Outcome.Done -> Result.success()
            DownloadTransfer.Outcome.Retry -> Result.retry()
        }
    }

    /** Set when Android wouldn't let this transfer show as a foreground service: not asked again. */
    private var promotionRefused = false

    /**
     * Shows the transfer as a foreground service, so it can run past Android's ten minutes for background work. Started
     * from the background (the next episode fetched by itself, a retry, a podcast's newest), Android 12 on may refuse,
     * and Android 15 on allows data sync six hours a day: the transfer then runs as ordinary background work, and if
     * it's stopped it starts again later from where it got to.
     */
    private suspend fun promote(info: ForegroundInfo) {
        if (promotionRefused) return
        try {
            setForeground(info)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            promotionRefused = true
            flexarrLog.warning("Couldn't show the download as a foreground service; carrying on in the background", e)
        }
    }

    override suspend fun getForegroundInfo(): ForegroundInfo {
        val local = inputData.getString(ID)?.let { id -> onMain { store.byId(id) } }
        return foregroundInfo(local, "Downloading")
    }

    private fun foregroundInfo(download: LocalDownload?, status: String, fraction: Double? = null): ForegroundInfo {
        val notification = DownloadTransfer.notification(applicationContext, download, status, fraction)
        val notificationId = DownloadTransfer.notificationId(download?.id)
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ForegroundInfo(notificationId, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        } else {
            ForegroundInfo(notificationId, notification)
        }
    }

    companion object {
        const val ID = "id"
    }
}
