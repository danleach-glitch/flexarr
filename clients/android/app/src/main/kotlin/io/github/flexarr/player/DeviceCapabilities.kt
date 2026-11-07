package io.github.flexarr.player

import android.content.Context
import android.hardware.display.DisplayManager
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.os.Build
import android.view.Display
import androidx.annotation.OptIn
import androidx.media3.common.C
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.audio.AudioCapabilities
import io.github.flexarr.core.DeviceProfile
import io.github.flexarr.core.ServerVersion
import io.github.flexarr.data.SoundSettings

/**
 * What this device plays as it is, sent with every playback request so the server knows whether to send the file or
 * convert it. ExoPlayer reads the common containers itself; the codecs are whatever this device has decoders for, plus
 * the surround formats its HDMI output takes as they are (Lossless surround). Anything else comes as HLS.
 */
object DeviceCapabilities {
    /** What the device and what's connected to it say, asked of the system: the input to [profile], and what Settings › Diagnostics shows. */
    data class Facts(
        /** Decodable types, lower case (`video/hevc`, `audio/ac3`). */
        val decoders: Set<String>,
        /** Video types with a decoder that takes 3840×2160. */
        val uhdDecoders: Set<String>,
        /** `Display.HdrCapabilities.HDR_TYPE_*` the screen takes in any of its modes. */
        val hdrTypes: Set<Int>,
        /** Surround formats the output takes as they are (`ac3`, `eac3`, `eac3-joc`, `dts`, `dts-hd`, `truehd`), by name. */
        val passthrough: Set<String>,
        /** Lossless surround is on and nothing that works on decoded sound (dialogue boost, night mode, speakers) is. */
        val passesThrough: Boolean,
    )

    /** The types this device has decoders for, asked of the system once: going through them all takes a while. */
    private val codecs: List<MediaCodecInfo> by lazy { MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filterNot { it.isEncoder } }

    private val decoders: Set<String> by lazy { codecs.flatMap { it.supportedTypes.asList() }.map { it.lowercase() }.toSet() }

    private val uhdDecoders: Set<String> by lazy {
        codecs.flatMap { info ->
            info.supportedTypes.filter { it.startsWith("video/") }.filter { type ->
                runCatching { info.getCapabilitiesForType(type).videoCapabilities?.isSizeSupported(3840, 2160) == true }.getOrDefault(false)
            }.map { it.lowercase() }
        }.toSet()
    }

    fun facts(context: Context): Facts {
        val sound = SoundSettings.current(context)
        return Facts(
            decoders = decoders,
            uhdDecoders = uhdDecoders,
            hdrTypes = hdrTypes(context),
            passthrough = passthrough(context),
            passesThrough = !sound.decodesSurround,
        )
    }

    fun profile(context: Context): DeviceProfile = profile(facts(context))

    /** The profile for what [facts] says: kept apart from the system so it can be tested. */
    fun profile(facts: Facts): DeviceProfile {
        val decoders = facts.decoders
        fun having(vararg pairs: Pair<String, String>) = pairs.filter { (mime, _) -> mime in decoders }.map { it.second }

        val hdr10 = HDR10 in facts.hdrTypes
        val hlg = HLG in facts.hdrTypes
        val dolbyVision = DOLBY_VISION in facts.hdrTypes && "video/dolby-vision" in decoders
        val decoded = having(
            "audio/mp4a-latm" to "aac",
            "audio/mpeg" to "mp3",
            "audio/opus" to "opus",
            "audio/vorbis" to "vorbis",
            "audio/flac" to "flac",
            "audio/ac3" to "ac3",
            "audio/eac3" to "eac3",
        )
        // What the output takes as it is goes as it is: DTS and TrueHD files then play directly (HLS can't carry them).
        val passedThrough = if (facts.passesThrough) facts.passthrough.mapNotNull { PASSTHROUGH_CODECS[it] }.distinct() else emptyList()

        return DeviceProfile(
            client = "android",
            containers = listOf("mp4", "m4v", "mov", "mkv", "webm", "mpegts", "ts"),
            videoCodecs = having(
                "video/avc" to "h264",
                "video/hevc" to "hevc",
                "video/x-vnd.on2.vp9" to "vp9",
                "video/av01" to "av1",
            ),
            audioCodecs = (decoded + passedThrough).distinct(),
            hdr = hdr10 || hlg || dolbyVision,
            // A 1080p screen still takes 4K files when a decoder does: it scales them itself, so the server needn't convert.
            maxHeight = if (facts.uhdDecoders.any { it in UHD_VIDEO }) 2160 else 1080,
            // Android mixes surround down for headphones and speakers itself.
            maxAudioChannels = 8,
            maxBitrate = 0,
            // Fragmented MP4 (lossless FLAC and Atmos in converted streams) only from servers that start its segments at
            // zero: see [withServer].
            fmp4 = false,
            dolbyVision = dolbyVision,
            hdr10 = hdr10,
            hlg = hlg,
        )
    }

    /**
     * [profile] for the server at [version]. Before 0.53.0 a server's fragmented MP4 segments could start a little
     * before zero (an audio priming offset), which ExoPlayer refuses ("Top bit not zero" in tfdt), so older servers
     * send MPEG-TS instead.
     */
    fun withServer(profile: DeviceProfile, version: String?): DeviceProfile =
        profile.copy(fmp4 = version != null && ServerVersion.atLeast(version, 0, 53))

    /** Every HDR type the screen takes in any of its modes: a TV sitting at 1080p SDR still switches for an HDR film. */
    private fun hdrTypes(context: Context): Set<Int> {
        val display = context.getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY) ?: return emptySet()
        val fromModes = if (Build.VERSION.SDK_INT >= 34) display.supportedModes.flatMap { it.supportedHdrTypes.asList() }.toSet() else emptySet()
        @Suppress("DEPRECATION") // Display.Mode's HDR types are Android 14 and later only.
        val fromDisplay = display.hdrCapabilities?.supportedHdrTypes?.toSet().orEmpty()
        return fromModes + fromDisplay
    }

    /** The surround formats the output (HDMI, an amplifier, an eARC soundbar) takes as they are, by name. */
    @OptIn(UnstableApi::class)
    fun passthrough(context: Context): Set<String> {
        val capabilities = runCatching { AudioCapabilities.getCapabilities(context) }.getOrNull() ?: return emptySet()
        return ENCODINGS.filter { (encoding, _) -> capabilities.supportsEncoding(encoding) }.map { it.second }.toSet()
    }

    const val HDR10 = 2 // Display.HdrCapabilities.HDR_TYPE_HDR10
    const val HLG = 3 // HDR_TYPE_HLG
    const val HDR10_PLUS = 4 // HDR_TYPE_HDR10_PLUS
    const val DOLBY_VISION = 1 // HDR_TYPE_DOLBY_VISION

    private val UHD_VIDEO = setOf("video/hevc", "video/av01", "video/x-vnd.on2.vp9", "video/avc")

    @OptIn(UnstableApi::class)
    private val ENCODINGS = listOf(
        C.ENCODING_AC3 to "ac3",
        C.ENCODING_E_AC3 to "eac3",
        C.ENCODING_E_AC3_JOC to "eac3-joc",
        C.ENCODING_DTS to "dts",
        C.ENCODING_DTS_HD to "dts-hd",
        C.ENCODING_DOLBY_TRUEHD to "truehd",
    )

    /** Each passed-through format's codec name for the server (ffprobe's: DTS-HD is `dts`, Atmos is `eac3`). */
    private val PASSTHROUGH_CODECS = mapOf(
        "ac3" to "ac3", "eac3" to "eac3", "eac3-joc" to "eac3", "dts" to "dts", "dts-hd" to "dts", "truehd" to "truehd",
    )
}
