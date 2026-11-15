package io.github.flexarr.core

import io.github.flexarr.core.i18n.t
import java.util.Locale

/**
 * The names the player's Audio menu shows, one per track in the same order. The server's label is the language plus
 * the stream title, so two untagged tracks both read "Unknown": a track whose language is unknown, or whose label
 * another track shares, gets its codec and channels too ("Unknown (MP3 Stereo)"), and any still alike are numbered.
 */
fun audioTrackLabels(tracks: List<AudioTrack>): List<String> {
    val counts = tracks.groupingBy { it.label }.eachCount()
    val described = tracks.map { track ->
        if (track.language.isNullOrBlank() || (counts[track.label] ?: 0) > 1) {
            val details = audioDetails(track)
            if (details.isEmpty()) track.label else t("{label} ({details})", "label" to track.label, "details" to details)
        } else {
            track.label
        }
    }
    val stillAlike = described.groupingBy { it }.eachCount()
    return described.mapIndexed { position, label ->
        if ((stillAlike[label] ?: 0) > 1) t("{label}, track {number}", "label" to label, "number" to position + 1) else label
    }
}

/** "MP3 Stereo", "AC3 5.1": the codec and channels, whichever are known. */
private fun audioDetails(track: AudioTrack): String {
    val codec = track.codec.takeIf { it.isNotBlank() }?.uppercase(Locale.ROOT)
    val channels = track.channels?.takeIf { it > 0 }?.let(::channelsLabel)
    return when {
        codec != null && channels != null -> t("{codec} {channels}", "codec" to codec, "channels" to channels)
        else -> codec ?: channels ?: ""
    }
}

/** "5.1", "Stereo", "Mono". */
fun channelsLabel(count: Int): String = when (count) {
    1 -> t("Mono")
    2 -> t("Stereo")
    6 -> "5.1"
    8 -> "7.1"
    else -> t("{count} ch", "count" to count)
}
