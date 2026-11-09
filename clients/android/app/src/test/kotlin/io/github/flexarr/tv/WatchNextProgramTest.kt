package io.github.flexarr.tv

import android.annotation.SuppressLint
import android.app.Application
import android.content.ContentValues
import androidx.tvprovider.media.tv.TvContractCompat
import io.github.flexarr.AppLink
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

/** What a Watch Next card writes: an update only changes the columns it's given. */
@RunWith(RobolectricTestRunner::class)
@Config(application = Application::class, sdk = [34])
class WatchNextProgramTest {
    private fun card(position: Int?, image: WatchNext.Picture?) = WatchNext.Card(
        "a", TvContractCompat.WatchNextPrograms.WATCH_NEXT_TYPE_NEXT, TvContractCompat.PreviewPrograms.TYPE_TV_EPISODE, "Title",
        positionMillis = position, lastEngagement = 1L, image = image, link = AppLink.Play("a"),
    )

    @SuppressLint("RestrictedApi")
    private fun values(card: WatchNext.Card): ContentValues = WatchNext.program(RuntimeEnvironment.getApplication(), card, "p/a").toContentValues()

    @Test fun anEpisodeWithItsPositionReset_clearsItsProgressBar() {
        val values = values(card(position = null, image = null))
        assertEquals(0, values.getAsInteger(TvContractCompat.WatchNextPrograms.COLUMN_LAST_PLAYBACK_POSITION_MILLIS))
    }

    @Test fun aTitleWithNoPictureNow_clearsItsArt() {
        val values = values(card(position = null, image = null))
        assertTrue(values.containsKey(TvContractCompat.WatchNextPrograms.COLUMN_POSTER_ART_URI))
        assertNull(values.getAsString(TvContractCompat.WatchNextPrograms.COLUMN_POSTER_ART_URI))
    }

    @Test fun positionAndArt_areWritten() {
        val values = values(card(position = 5_000, image = WatchNext.Picture("https://server/a.jpg", TvContractCompat.PreviewPrograms.ASPECT_RATIO_16_9)))
        assertEquals(5_000, values.getAsInteger(TvContractCompat.WatchNextPrograms.COLUMN_LAST_PLAYBACK_POSITION_MILLIS))
        assertEquals("https://server/a.jpg", values.getAsString(TvContractCompat.WatchNextPrograms.COLUMN_POSTER_ART_URI))
    }
}
