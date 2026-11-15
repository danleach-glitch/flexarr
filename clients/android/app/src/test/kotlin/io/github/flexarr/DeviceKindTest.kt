package io.github.flexarr

import androidx.media3.common.PlaybackException
import io.github.flexarr.player.PlaybackController
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * The words name the device in hand: "this tablet" on a tablet, "this TV" on a TV, "this phone" on a phone (#502).
 * Robolectric, for the clock a PlaybackException reads; a plain Application, so the app's own doesn't start.
 */
@RunWith(RobolectricTestRunner::class)
@Config(application = android.app.Application::class)
class DeviceKindTest {
    @Test
    fun `a tablet by its narrowest side, a TV by its mode`() {
        assertEquals(DeviceKind.Phone, DeviceKind.of(isTelevision = false, smallestScreenWidthDp = 411))
        assertEquals(DeviceKind.Phone, DeviceKind.of(isTelevision = false, smallestScreenWidthDp = 599))
        assertEquals(DeviceKind.Tablet, DeviceKind.of(isTelevision = false, smallestScreenWidthDp = 600))
        assertEquals(DeviceKind.Tablet, DeviceKind.of(isTelevision = false, smallestScreenWidthDp = 800))
        assertEquals(DeviceKind.Tv, DeviceKind.of(isTelevision = true, smallestScreenWidthDp = 540))
        assertEquals(DeviceKind.Tv, DeviceKind.of(isTelevision = true, smallestScreenWidthDp = 960))
    }

    @Test
    fun `the player's errors name the device`() {
        val decoder = PlaybackException("decoder", null, PlaybackException.ERROR_CODE_DECODING_FAILED)
        assertEquals("This phone couldn’t decode the picture or the sound.", PlaybackController.playerErrorMessage(decoder, device = DeviceKind.Phone))
        assertEquals("This tablet couldn’t decode the picture or the sound.", PlaybackController.playerErrorMessage(decoder, device = DeviceKind.Tablet))
        assertEquals("This TV couldn’t decode the picture or the sound.", PlaybackController.playerErrorMessage(decoder, device = DeviceKind.Tv))
        val lost = PlaybackException("lost", null, PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED)
        assertEquals(
            "Lost the connection to the server. Make sure this tablet is online and Flexarr is running, then try again.",
            PlaybackController.playerErrorMessage(lost, device = DeviceKind.Tablet),
        )
    }
}
