package io.github.flexarr.core

import io.github.flexarr.core.i18n.Localization
import io.github.flexarr.core.i18n.t
import kotlinx.serialization.Serializable
import java.text.NumberFormat

/**
 * What a converted or repackaged stream carries (PlaybackInfo.stream), for the player's line under the title. As the
 * server's StreamSummaryDto.
 */
@Serializable
data class StreamSummary(
    /** The picture is the file's own, repackaged; false when it's converted. */
    val videoCopied: Boolean = false,
    /** "hevc" or "h264": the file's when copied, H.264 when converted. */
    val videoCodec: String = "h264",
    /** A copied picture's: "DolbyVision", "HDR10", "HLG" or "SDR". Null for a converted one. */
    val dynamicRange: String? = null,
    /** A copied HDR10 picture carries HDR10+ too. */
    val hdr10Plus: Boolean = false,
    /** A converted picture's height, in pixels. */
    val height: Int? = null,
    /** A converted picture's bit rate, in bits per second. */
    val videoBitrate: Long? = null,
    /** A converted picture's HDR was turned into SDR. */
    val toneMapped: Boolean = false,
    /** The sound is converted to lossless FLAC. */
    val losslessAudio: Boolean = false,
    /** How many channels the sound has when it's converted to FLAC. */
    val audioChannels: Int? = null,
)

/**
 * The player's line for a converted or repackaged stream, in the language shown ("Original HEVC (HDR10), repackaged",
 * "Converting: 1080p H.264 at 8 Mbps"); null when the file plays as it is. A server before 0.66 sends only its English
 * summary, used as it is. As the web player's streamBadge.
 */
fun streamLine(info: PlaybackInfo): String? {
    val stream = info.stream
    if (stream == null) {
        val summary = info.transcodeSummary?.takeIf { it.isNotBlank() } ?: return null
        return if (summary.startsWith("Original")) summary else t("Converting: {summary}", "summary" to summary)
    }
    val video = if (stream.videoCopied) {
        // Names, not words: as the server's summary writes them. HDR10+ travels inside the HDR10.
        val range = when (stream.dynamicRange) {
            "DolbyVision" -> if (stream.hdr10Plus) "Dolby Vision, HDR10+" else "Dolby Vision"
            "HDR10" -> if (stream.hdr10Plus) "HDR10+" else "HDR10"
            "HLG" -> "HLG"
            else -> null
        }
        val codec = if (stream.videoCodec == "hevc") "HEVC" else "H.264"
        t("Original {video}, repackaged", "video" to (if (range != null) "$codec ($range)" else codec))
    } else {
        val mbps = NumberFormat.getNumberInstance(Localization.locale).apply { maximumFractionDigits = 1 }.format((stream.videoBitrate ?: 0) / 1_000_000.0)
        val height = (stream.height ?: 0).toString()
        if (stream.toneMapped) t("Converting: {height}p H.264 at {mbps} Mbps, HDR converted to SDR", "height" to height, "mbps" to mbps)
        else t("Converting: {height}p H.264 at {mbps} Mbps", "height" to height, "mbps" to mbps)
    }
    return if (stream.losslessAudio) t("{video}, lossless FLAC {channels} sound", "video" to video, "channels" to channels(stream.audioChannels)) else video
}

private fun channels(count: Int?): String = when (count) {
    8 -> "7.1"
    6 -> "5.1"
    2 -> t("stereo")
    1 -> t("mono")
    else -> t("{count}-channel", "count" to (count ?: 0))
}
