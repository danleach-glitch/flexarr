package io.github.flexarr.core

// Settings › Playback, as FlexarrKit's playbackPreferences(): the profile's auto-play, skips and subtitle defaults,
// followed by every device. Saving is FlexarrClient.setPlaybackPreferences.

/** The signed-in profile's auto-play, skip and subtitle preferences. */
suspend fun FlexarrClient.playbackPreferences(): PlaybackPreferences = get("me/playback-preferences")

/**
 * As it's safe to send back: a size or position this app doesn't know (from a newer server) is left out, so the
 * server keeps the profile's own rather than being told an empty one.
 */
fun PlaybackPreferences.forSaving(): PlaybackPreferences = copy(
    subtitleSize = subtitleSize?.takeIf { it != SubtitleSize.Unknown },
    subtitlePosition = subtitlePosition?.takeIf { it != SubtitlePosition.Unknown },
)

/** The size subtitles are drawn at, as Settings shows it: Standard when the server doesn't say, or says something new. */
val PlaybackPreferences.shownSubtitleSize: SubtitleSize
    get() = subtitleSize?.takeIf { it != SubtitleSize.Unknown } ?: SubtitleSize.Standard

/** Where subtitles sit, as Settings shows it: at the bottom when the server doesn't say, or says something new. */
val PlaybackPreferences.shownSubtitlePosition: SubtitlePosition
    get() = subtitlePosition?.takeIf { it != SubtitlePosition.Unknown } ?: SubtitlePosition.Bottom
