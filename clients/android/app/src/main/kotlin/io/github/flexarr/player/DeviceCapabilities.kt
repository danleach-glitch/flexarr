package io.github.flexarr.player

import android.content.Context
import android.hardware.display.DisplayManager
import android.media.MediaCodecList
import android.view.Display
import io.github.flexarr.core.DeviceProfile
import io.github.flexarr.core.ServerVersion

/**
 * What this device plays as it is, sent with every playback request so the server knows whether to send the file or
 * convert it. ExoPlayer reads the common containers itself; the codecs are whatever this device has decoders for.
 * Anything else comes as HLS.
 */
object DeviceCapabilities {
    fun profile(context: Context): DeviceProfile {
        val decoders = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
            .filterNot { it.isEncoder }
            .flatMap { it.supportedTypes.asList() }
            .map { it.lowercase() }
            .toSet()
        fun having(vararg pairs: Pair<String, String>) = pairs.filter { (mime, _) -> mime in decoders }.map { it.second }

        @Suppress("DEPRECATION") // Display.Mode's HDR types are Android 14 and later only.
        val hdrTypes = context.getSystemService(DisplayManager::class.java)
            .getDisplay(Display.DEFAULT_DISPLAY)?.hdrCapabilities?.supportedHdrTypes?.toSet().orEmpty()
        val hdr10 = Display.HdrCapabilities.HDR_TYPE_HDR10 in hdrTypes
        val hlg = Display.HdrCapabilities.HDR_TYPE_HLG in hdrTypes
        val dolbyVision = Display.HdrCapabilities.HDR_TYPE_DOLBY_VISION in hdrTypes && "video/dolby-vision" in decoders

        return DeviceProfile(
            client = "android",
            containers = listOf("mp4", "m4v", "mov", "mkv", "webm", "mpegts", "ts"),
            videoCodecs = having(
                "video/avc" to "h264",
                "video/hevc" to "hevc",
                "video/x-vnd.on2.vp9" to "vp9",
                "video/av01" to "av1",
            ),
            audioCodecs = having(
                "audio/mp4a-latm" to "aac",
                "audio/mpeg" to "mp3",
                "audio/opus" to "opus",
                "audio/vorbis" to "vorbis",
                "audio/flac" to "flac",
                "audio/ac3" to "ac3",
                "audio/eac3" to "eac3",
            ),
            hdr = hdr10 || hlg || dolbyVision,
            maxHeight = 2160,
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
}
