package io.github.flexarr.downloads

import io.github.flexarr.logging.FlexarrLog
import android.annotation.SuppressLint
import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import android.os.Build
import android.os.PersistableBundle
import androidx.annotation.RequiresApi
import androidx.core.content.edit
import io.github.flexarr.appModel
import io.github.flexarr.core.LocalDownload
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

private val flexarrLog = FlexarrLog("downloads")

/**
 * A download asked for by hand, on Android 14 on: run as a user-initiated data transfer job, which Android lets run for
 * hours with the app closed and shows in its notification and Task Manager. It's the transfer [DownloadWorker] runs,
 * without the foreground service that Android 12 on won't start from the background, nor Android 15's six hours a day
 * for data sync. Retries come back through the job (its own backoff); the user stopping it in Task Manager stops it.
 */
// Its jobs have a namespace of their own, so their ids can't meet WorkManager's.
@SuppressLint("SpecifyJobSchedulerIdRange")
@RequiresApi(Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
class DownloadJobService : JobService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val transfers = mutableMapOf<Int, Job>()

    override fun onStartJob(params: JobParameters): Boolean {
        val id = params.extras.getString(UserInitiatedTransfers.ID) ?: return false
        val store = appModel.downloads
        // Android asks for the notification straight away (within seconds) and shows it while the job runs.
        notify(params, store.byId(id), "Getting ready")
        transfers[params.jobId] = scope.launch {
            val transfer = DownloadTransfer(
                this@DownloadJobService,
                show = { download, status, fraction -> notify(params, download, status, fraction) },
                transferred = { written, total ->
                    if (total != null) updateEstimatedNetworkBytes(params, total, 0)
                    updateTransferredNetworkBytes(params, written, 0)
                },
            )
            val outcome = transfer.run(id, UserInitiatedTransfers.attempts(this@DownloadJobService, id))
            UserInitiatedTransfers.attempted(this@DownloadJobService, id, again = outcome == DownloadTransfer.Outcome.Retry)
            transfers.remove(params.jobId)
            jobFinished(params, outcome == DownloadTransfer.Outcome.Retry)
        }
        return true
    }

    /**
     * Stopped before it finished: deleted (the app cancelled it), stopped in Task Manager (it fails, to be tried again by
     * hand: Android won't let the app start it again by itself), or for the network or the system's sake (it starts again
     * later, from where it got to).
     */
    override fun onStopJob(params: JobParameters): Boolean {
        transfers.remove(params.jobId)?.cancel()
        return when (params.stopReason) {
            JobParameters.STOP_REASON_CANCELLED_BY_APP -> false
            JobParameters.STOP_REASON_USER -> {
                params.extras.getString(UserInitiatedTransfers.ID)?.let { appModel.downloads.failed(it, "The download was stopped. Try again.") }
                false
            }
            else -> true
        }
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private fun notify(params: JobParameters, download: LocalDownload?, status: String, fraction: Double? = null) {
        val id = params.extras.getString(UserInitiatedTransfers.ID)
        setNotification(params, DownloadTransfer.notificationId(id), DownloadTransfer.notification(this, download, status, fraction), JOB_END_NOTIFICATION_POLICY_REMOVE)
    }
}

/**
 * Scheduling downloads as user-initiated data transfer jobs ([DownloadJobService]), on Android 14 on. Android takes one
 * only while the app is on screen or the user has just acted on it (a notification's button counts: the broadcast it
 * sends lets the app start activities for a few seconds, which is what Android checks); otherwise [schedule] says no
 * and the transfer goes to WorkManager. The jobs have a namespace of their own, apart from WorkManager's.
 */
internal object UserInitiatedTransfers {
    const val ID = "id"
    private const val NAMESPACE = "downloads"
    private const val BACKOFF_MS = 30_000L

    /** Schedules [download]'s transfer as a user-initiated job; false when Android won't take one now. */
    fun schedule(context: Context, download: LocalDownload): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE) return false
        val scheduler = scheduler(context)
        if (!scheduler.canRunUserInitiatedJobs()) return false
        val job = JobInfo.Builder(jobId(download.id), ComponentName(context, DownloadJobService::class.java))
            .setUserInitiated(true)
            .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
            .setRequiresStorageNotLow(true)
            .setPersisted(true)
            .setBackoffCriteria(BACKOFF_MS, JobInfo.BACKOFF_POLICY_EXPONENTIAL)
            .setExtras(PersistableBundle().apply { putString(ID, download.id) })
            .apply { download.sizeBytes?.let { setEstimatedNetworkBytes(it, 0) } }
            .build()
        return try {
            (scheduler.schedule(job) == JobScheduler.RESULT_SUCCESS).also { scheduled ->
                if (scheduled) attempted(context, download.id, again = false)
                else flexarrLog.info("Android didn't take ${download.id} as a user-initiated job; it runs through WorkManager")
            }
        } catch (e: RuntimeException) {
            // Not allowed from where the app is now (some versions throw rather than say no).
            flexarrLog.warning("Couldn't schedule a user-initiated download", e)
            false
        }
    }

    /** A job is waiting or running for download [id]. */
    fun isScheduled(context: Context, id: String): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE) return false
        return scheduler(context).getPendingJob(jobId(id))?.extras?.getString(ID) == id
    }

    fun cancel(context: Context, id: String) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE) return
        val scheduler = scheduler(context)
        if (scheduler.getPendingJob(jobId(id))?.extras?.getString(ID) == id) scheduler.cancel(jobId(id))
        attempted(context, id, again = false)
    }

    fun cancelAll(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE) return
        scheduler(context).cancelAll()
        attempts(context).edit { clear() }
    }

    /** How many times download [id]'s job has stopped short and been tried again (it gives up after a while). */
    fun attempts(context: Context, id: String): Int = attempts(context).getInt(id, 0)

    fun attempted(context: Context, id: String, again: Boolean) {
        attempts(context).edit { if (again) putInt(id, attempts(context, id) + 1) else remove(id) }
    }

    private fun attempts(context: Context) = context.getSharedPreferences("downloadJobs", Context.MODE_PRIVATE)

    /** A job's number in the namespace: from the download's id (checked against the id the job carries). */
    private fun jobId(id: String): Int = id.hashCode()

    @RequiresApi(Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
    private fun scheduler(context: Context): JobScheduler = context.getSystemService(JobScheduler::class.java).forNamespace(NAMESPACE)
}
