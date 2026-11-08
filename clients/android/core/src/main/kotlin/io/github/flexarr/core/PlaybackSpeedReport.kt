package io.github.flexarr.core

import kotlin.math.abs

/**
 * When the server hears a video's speed. An episode's is kept for its show (for this profile); a film's isn't kept,
 * but while its stream is being converted the server makes it as fast as it's watched, so it's told too. Otherwise a
 * converted film watched at 1.5× or 2× uses up the server's head start within minutes and then waits for every
 * segment. Servers before 0.53 have no speed endpoint; they also send no player controls, so they're never asked.
 */
object PlaybackSpeedReport {
    /**
     * Whether to send [rate] for a title of [kind], when the server already has it at [known] (the rate before a
     * change, or when a stream opens, the show's kept speed it gave, else 1).
     */
    fun shouldTell(
        kind: MediaKind,
        rate: Float,
        known: Double,
        converted: Boolean,
        hasPlayerControls: Boolean,
        offline: Boolean,
        recordsHistory: Boolean,
    ): Boolean {
        if (!hasPlayerControls || abs(rate - known) < 0.001) return false
        return when (kind) {
            // Kept for the show: not while trying things out without history.
            MediaKind.Episode -> recordsHistory
            // These keep a speed of their own on the server; the video player never plays them.
            MediaKind.Series, MediaKind.Season, MediaKind.Audiobook -> false
            // A film: only its converted stream cares, and a download has no stream.
            else -> converted && !offline
        }
    }
}
