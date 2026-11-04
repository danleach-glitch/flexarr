package io.github.flexarr

import android.content.Context
import android.os.Build
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.IntentSenderRequest
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.google.android.play.core.appupdate.AppUpdateManager
import com.google.android.play.core.appupdate.AppUpdateManagerFactory
import com.google.android.play.core.appupdate.AppUpdateOptions
import com.google.android.play.core.install.InstallStateUpdatedListener
import com.google.android.play.core.install.model.AppUpdateType
import com.google.android.play.core.install.model.InstallStatus
import com.google.android.play.core.install.model.UpdateAvailability
import io.github.flexarr.logging.FlexarrLog
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.tasks.await

private val log = FlexarrLog("update")

/**
 * Google Play's in-app updates, the flexible kind: at launch (and back in front) the app asks Play whether there's a
 * newer version, and if so Play asks whether to download it while the app carries on. Once it's downloaded, [isReady]
 * and the app offers Restart to update ([restart]). Nothing happens in debug builds or when the app didn't come from
 * Google Play ([applies]).
 */
class AppUpdates(private val manager: AppUpdateManager, private val enabled: Boolean) {
    /** A downloaded update is waiting for a restart. */
    var isReady by mutableStateOf(false)
        private set

    /** Asked once a launch: saying no to Play's download prompt isn't met with it again on every return. */
    private var asked = false

    private val listener = InstallStateUpdatedListener { state ->
        if (state.installStatus() == InstallStatus.DOWNLOADED) isReady = true
    }

    init {
        if (enabled) manager.registerListener(listener)
    }

    /** Asks Play about updates, starting the download prompt through [launcher] when there's one to offer. */
    suspend fun check(launcher: ActivityResultLauncher<IntentSenderRequest>) {
        if (!enabled) return
        try {
            val info = manager.appUpdateInfo.await()
            when {
                info.installStatus() == InstallStatus.DOWNLOADED -> isReady = true
                !asked && info.updateAvailability() == UpdateAvailability.UPDATE_AVAILABLE && info.isUpdateTypeAllowed(AppUpdateType.FLEXIBLE) -> {
                    asked = true
                    manager.startUpdateFlowForResult(info, launcher, AppUpdateOptions.defaultOptions(AppUpdateType.FLEXIBLE))
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            // No Play Store on this device, or no answer from it: try again next launch.
            log.info("No update check", e)
        }
    }

    /** Installs the downloaded update; Play restarts the app into it. */
    fun restart() {
        if (isReady) manager.completeUpdate()
    }

    fun close() {
        if (enabled) manager.unregisterListener(listener)
    }

    companion object {
        private const val PLAY_STORE = "com.android.vending"

        /** Updates come from Play only to an app Play installed, and never to a debug build. */
        fun applies(installer: String?, debug: Boolean): Boolean = !debug && installer == PLAY_STORE

        fun create(context: Context): AppUpdates =
            AppUpdates(AppUpdateManagerFactory.create(context), applies(installer(context), BuildConfig.DEBUG))

        private fun installer(context: Context): String? = runCatching {
            if (Build.VERSION.SDK_INT >= 30) {
                context.packageManager.getInstallSourceInfo(context.packageName).installingPackageName
            } else {
                @Suppress("DEPRECATION")
                context.packageManager.getInstallerPackageName(context.packageName)
            }
        }.getOrNull()
    }
}
