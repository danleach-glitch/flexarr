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
        heights: Map<String, Int> = mapOf("video/hevc" to 2160),
        hdr: Set<Int> = emptySet(),
        passthrough: Set<String> = emptySet(),
        passesThrough: Boolean = true,
    ) = DeviceCapabilities.Facts(decoders, heights, hdr, passthrough, passesThrough)

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
        assertEquals(2160, DeviceCapabilities.profile(facts(heights = mapOf("video/hevc" to 2160))).maxHeight)
        assertEquals(1080, DeviceCapabilities.profile(facts(heights = emptyMap())).maxHeight)
        // An image decoder that takes 4K doesn't count.
        assertEquals(1080, DeviceCapabilities.profile(facts(heights = mapOf("video/mjpeg" to 2160))).maxHeight)
    }

    @Test fun fourKH264_saysNothingAboutHevc() {
        val heights = mapOf("video/avc" to 2160, "video/hevc" to 1080)
        assertEquals(1080, DeviceCapabilities.profile(facts(heights = heights)).maxHeight)
    }

    @Test fun hevcOnlyInSoftware_hasNoSay() {
        // In the decoders, but not the hardware's: the 4K files the hardware plays aren't sent converted.
        assertEquals(2160, DeviceCapabilities.profile(facts(heights = mapOf("video/avc" to 2160))).maxHeight)
        assertEquals(2160, DeviceCapabilities.profile(facts(heights = mapOf("video/x-vnd.on2.vp9" to 2160, "video/avc" to 1080))).maxHeight)
    }

    @Test fun fourKHevc_isntHeldBackByH264() {
        assertEquals(2160, DeviceCapabilities.profile(facts(heights = mapOf("video/avc" to 1080, "video/hevc" to 2160))).maxHeight)
    }

    @Test fun withoutHevc_h264Decides() {
        val decoders = setOf("video/avc")
        assertEquals(2160, DeviceCapabilities.profile(facts(decoders = decoders, heights = mapOf("video/avc" to 2160))).maxHeight)
    }

    @Test fun aPhoneLimitedTo1440p_takes1440pFiles() {
        assertEquals(1440, DeviceCapabilities.profile(facts(heights = mapOf("video/hevc" to 1440))).maxHeight)
    }

    @Test fun aDecodersHeight_isTheSizeItReallyTakes() {
        fun upTo(maxWidth: Int, maxHeight: Int) = DeviceCapabilities.tallest { width, height -> width <= maxWidth && height <= maxHeight }
        assertEquals(2160, upTo(4096, 2304))
        assertEquals(1440, upTo(2560, 1440))
        assertEquals(1080, upTo(1920, 1088))
        assertEquals(null, upTo(1280, 720))
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

    @Test fun surround_hasShortNamesForItsRow() {
        assertEquals(listOf("DD", "DD+", "Atmos", "DTS", "DTS-HD", "TrueHD"), DeviceCapabilities.surroundShortNames(setOf("truehd", "dts-hd", "dts", "eac3-joc", "eac3", "ac3")))
    }

    @Test fun refreshRates_followTheLanguageOnScreen() {
        assertEquals("59.94", DeviceCapabilities.rate(59.94f))
        assertEquals("23.976", DeviceCapabilities.rate(23.976f))
        assertEquals("60", DeviceCapabilities.rate(60f))
        assertEquals("59,94", DeviceCapabilities.rate(59.94f, java.util.Locale.GERMANY))
    }

    @Test fun aRecentAnswer_isKeptUntilItsTimeOrAChange() {
        var clock = 0L
        var asked = 0
        val recent = RecentAnswer<String, Int>(30_000) { clock }
        fun get(key: String) = recent.get(key) { ++asked }
        assertEquals(1, get("sound"))
        clock = 29_999
        assertEquals(1, get("sound"))
        // The sound settings changed.
        assertEquals(2, get("other sound"))
        // An audio device came or went.
        recent.clear()
        assertEquals(3, get("other sound"))
        clock += 30_000
        assertEquals(4, get("other sound"))
        // Play's own answer is what a warm-up goes on to ask with.
        recent.put("other sound", 99)
        assertEquals(99, get("other sound"))
    }
}
