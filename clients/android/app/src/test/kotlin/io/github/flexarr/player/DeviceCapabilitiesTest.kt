package io.github.flexarr.player

import io.github.flexarr.data.SoundSettings
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** What the server is told this device plays, from what the device says (docs/android-tv.md, stage 4). */
class DeviceCapabilitiesTest {
    private val tvDecoders = setOf("video/avc", "video/hevc", "video/av01", "audio/mp4a-latm", "audio/ac3", "audio/eac3", "audio/flac")

    private fun facts(
        decoders: Set<String> = tvDecoders,
        uhd: Set<String> = setOf("video/hevc"),
        hdr: Set<Int> = emptySet(),
        passthrough: Set<String> = emptySet(),
        passesThrough: Boolean = true,
    ) = DeviceCapabilities.Facts(decoders, uhd, hdr, passthrough, passesThrough)

    @Test fun hdrTypes_comeFromTheScreen() {
        val profile = DeviceCapabilities.profile(facts(hdr = setOf(DeviceCapabilities.HDR10, DeviceCapabilities.HLG, DeviceCapabilities.HDR10_PLUS)))
        assertTrue(profile.hdr)
        assertEquals(true, profile.hdr10)
        assertEquals(true, profile.hlg)
        assertEquals(false, profile.dolbyVision)
    }

    @Test fun sdrScreen_saysNoHdr() {
        val profile = DeviceCapabilities.profile(facts())
        assertFalse(profile.hdr)
        assertEquals(false, profile.hdr10)
    }

    @Test fun dolbyVision_needsItsDecoder() {
        val screenOnly = DeviceCapabilities.profile(facts(hdr = setOf(DeviceCapabilities.DOLBY_VISION)))
        assertEquals(false, screenOnly.dolbyVision)
        val both = DeviceCapabilities.profile(facts(decoders = tvDecoders + "video/dolby-vision", hdr = setOf(DeviceCapabilities.DOLBY_VISION)))
        assertEquals(true, both.dolbyVision)
        assertTrue(both.hdr)
    }

    @Test fun fourK_comesFromTheDecoders() {
        assertEquals(2160, DeviceCapabilities.profile(facts(uhd = setOf("video/hevc"))).maxHeight)
        assertEquals(1080, DeviceCapabilities.profile(facts(uhd = emptySet())).maxHeight)
        // An image decoder that takes 4K doesn't count.
        assertEquals(1080, DeviceCapabilities.profile(facts(uhd = setOf("video/mjpeg"))).maxHeight)
    }

    @Test fun passthrough_addsDtsAndTrueHd() {
        val profile = DeviceCapabilities.profile(facts(passthrough = setOf("ac3", "eac3", "eac3-joc", "dts", "dts-hd", "truehd")))
        assertEquals(listOf("aac", "flac", "ac3", "eac3", "dts", "truehd"), profile.audioCodecs)
    }

    @Test fun passthrough_onlyWhatTheOutputTakes() {
        val profile = DeviceCapabilities.profile(facts(decoders = setOf("audio/mp4a-latm"), passthrough = setOf("ac3")))
        assertEquals(listOf("aac", "ac3"), profile.audioCodecs)
    }

    @Test fun decodedSurround_leavesPassthroughOut() {
        val profile = DeviceCapabilities.profile(facts(passthrough = setOf("dts", "truehd"), passesThrough = false))
        assertEquals(listOf("aac", "flac", "ac3", "eac3"), profile.audioCodecs)
    }

    @Test fun soundFeatures_decodeSurround() {
        assertFalse(SoundSettings().decodesSurround)
        assertTrue(SoundSettings(losslessSurround = false).decodesSurround)
        assertTrue(SoundSettings(dialogueBoost = SoundSettings.DialogueBoost.Low).decodesSurround)
        assertTrue(SoundSettings(nightMode = SoundSettings.NightMode.Scheduled).decodesSurround)
        assertTrue(SoundSettings(speakers = SoundSettings.Speakers.Stereo).decodesSurround)
    }
}
