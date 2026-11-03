package io.github.flexarr

import android.app.Application
import androidx.activity.result.ActivityResultLauncher
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContract
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.app.ActivityOptionsCompat
import com.google.android.play.core.appupdate.testing.FakeAppUpdateManager
import com.google.android.play.core.install.model.AppUpdateType
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/** Google Play's in-app updates, against Play's own fake: when the download prompt comes, and when the restart does. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class)
class AppUpdatesTest {
    private val play = FakeAppUpdateManager(RuntimeEnvironment.getApplication())

    /** Play's prompt is the fake's to show; nothing is launched for real. */
    private val launcher = object : ActivityResultLauncher<IntentSenderRequest>() {
        override val contract: ActivityResultContract<IntentSenderRequest, *> = ActivityResultContracts.StartIntentSenderForResult()
        override fun launch(input: IntentSenderRequest, options: ActivityOptionsCompat?) {}
        override fun unregister() {}
    }

    @Test
    fun `no update, no prompt and nothing to restart into`() = runTest {
        play.setUpdateNotAvailable()
        val updates = AppUpdates(play, enabled = true)
        updates.check(launcher)
        assertFalse(play.isConfirmationDialogVisible)
        assertFalse(updates.isReady)
    }

    @Test
    fun `an update, Play asks to download it the flexible way, once a launch`() = runTest {
        play.setUpdateAvailable(99_000, AppUpdateType.FLEXIBLE)
        val updates = AppUpdates(play, enabled = true)
        updates.check(launcher)
        assertTrue(play.isConfirmationDialogVisible)
        play.userRejectsUpdate()
        updates.check(launcher)
        assertFalse(play.isConfirmationDialogVisible)
    }

    @Test
    fun `accepted and downloaded, the restart is offered and installs it`() = runTest {
        play.setUpdateAvailable(99_000, AppUpdateType.FLEXIBLE)
        val updates = AppUpdates(play, enabled = true)
        updates.check(launcher)
        play.userAcceptsUpdate()
        // The download started is the flexible kind, so the app carries on while it comes.
        assertEquals(AppUpdateType.FLEXIBLE, play.typeForUpdateInProgress)
        play.downloadStarts()
        assertFalse(updates.isReady)
        play.downloadCompletes()
        assertTrue(updates.isReady)
        updates.restart()
        assertTrue(play.isInstallSplashScreenVisible)
    }

    @Test
    fun `downloaded on an earlier launch, the restart is offered straight away`() = runTest {
        play.setUpdateAvailable(99_000, AppUpdateType.FLEXIBLE)
        AppUpdates(play, enabled = true).check(launcher)
        play.userAcceptsUpdate()
        play.downloadStarts()
        play.downloadCompletes()
        val next = AppUpdates(play, enabled = true)
        next.check(launcher)
        assertTrue(next.isReady)
    }

    @Test
    fun `not from Google Play, nothing`() = runTest {
        play.setUpdateAvailable(99_000, AppUpdateType.FLEXIBLE)
        val updates = AppUpdates(play, enabled = AppUpdates.applies(installer = "com.android.shell", debug = false))
        updates.check(launcher)
        assertFalse(play.isConfirmationDialogVisible)
        assertFalse(updates.isReady)
    }

    @Test
    fun `only a release build Play installed takes updates`() {
        assertTrue(AppUpdates.applies("com.android.vending", debug = false))
        assertFalse(AppUpdates.applies("com.android.vending", debug = true))
        assertFalse(AppUpdates.applies(null, debug = false))
        assertFalse(AppUpdates.applies("com.android.shell", debug = false))
    }
}
