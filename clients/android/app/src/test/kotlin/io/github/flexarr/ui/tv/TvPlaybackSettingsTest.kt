package io.github.flexarr.ui.tv

import android.app.Application
import android.content.Intent
import android.media.AudioFormat
import android.media.AudioManager
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.SemanticsNodeInteraction
import androidx.compose.ui.test.hasClickAction
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.junit4.createComposeRule
import io.github.flexarr.TestServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

/** Lossless surround's row on the real Playback page, as what's connected changes while the page is open. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [34], qualifiers = "w960dp-h1600dp")
class TvPlaybackSettingsTest {
    @get:Rule val rule = createComposeRule()

    private val server = TestServer { null }
    private val app get() = RuntimeEnvironment.getApplication()

    @After fun close() = server.close()

    private fun plugIn(vararg encodings: Int) {
        @Suppress("DEPRECATION")
        app.sendStickyBroadcast(
            Intent(AudioManager.ACTION_HDMI_AUDIO_PLUG)
                .putExtra(AudioManager.EXTRA_AUDIO_PLUG_STATE, 1)
                .putExtra(AudioManager.EXTRA_ENCODINGS, encodings),
        )
    }

    private fun losslessRow(says: String): SemanticsNodeInteraction =
        rule.onNode(hasClickAction() and hasContentDescription("Lossless surround", substring = true) and hasContentDescription(says, substring = true))

    private fun hdmiListeners() = shadowOf(app).registeredReceivers.count { it.intentFilter.hasAction(AudioManager.ACTION_HDMI_AUDIO_PLUG) }

    @Test
    fun aSoundbarSwitchedOnWhileThePageIsOpen_changesWhatTheRowSays_andAScreenReaderHearsIt() {
        var showing by mutableStateOf(true)
        val before = hdmiListeners()
        rule.setContent { if (showing) TvPlaybackSettings(server.model) }
        rule.waitForIdle()
        server.waitUntil("the page to load") { server.requests.isNotEmpty() }
        rule.waitForIdle()
        losslessRow("What’s connected takes no surround format as it is").assertExists()

        plugIn(AudioFormat.ENCODING_AC3, AudioFormat.ENCODING_E_AC3)
        shadowOf(android.os.Looper.getMainLooper()).idle()
        rule.waitForIdle()
        losslessRow("go to your receiver or soundbar").assertExists()

        rule.runOnIdle { showing = false }
        rule.waitForIdle()
        assertEquals("it stops listening when the page goes", before, hdmiListeners())
    }
}
