package io.github.flexarr.ui.downloads

import io.github.flexarr.core.DownloadQuality
import io.github.flexarr.core.LocalDownload
import io.github.flexarr.core.LocalPhase
import io.github.flexarr.core.PlaybackInfo
import io.github.flexarr.fixture
import org.junit.Assert.assertEquals
import org.junit.Test

/** A screen reader hears the same percentage the Download button's caption shows, not the one saved when it began. */
class DownloadDescriptionTest {
    private val coming = LocalDownload(
        id = "d",
        profileId = "p",
        quality = DownloadQuality.Original,
        item = fixture<PlaybackInfo>("playback").item,
        phase = LocalPhase.Downloading,
        progress = 0.0,
    )

    @Test
    fun saysTheLiveProgress() {
        assertEquals("Downloading, 39%", downloadDescription(coming, waitingForWiFi = false, progress = 0.39))
    }

    @Test
    fun fallsBackToTheSavedProgress() {
        assertEquals("Downloading, 16%", downloadDescription(coming.copy(progress = 0.16), waitingForWiFi = false))
    }
}
