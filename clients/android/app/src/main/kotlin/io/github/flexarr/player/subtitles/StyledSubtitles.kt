package io.github.flexarr.player.subtitles

import androidx.core.net.toUri
import androidx.annotation.OptIn
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.text.Cue
import androidx.media3.common.util.UnstableApi
import io.github.flexarr.core.SubtitleTrack

/**
 * Styled (ASS/SSA) subtitles, drawn as the iPhone draws them (SubtitleOverlayView.swift): a line goes where its tags ask
 * (`\an` alignment, `\pos`, the style's alignment and margins), in its style's colours, bold, italic, underline and
 * strike-through, and every other line at the profile's place; all at the profile's size. Media3 reads the server's
 * styled copy itself (its SSA parser), so nothing native is needed. What it leaves out, as the iPhone does: fonts
 * attached to the file, colours and bold or italic set partway through a line, outlines, rotation, fades, karaoke and
 * vector drawings (left out rather than shown as numbers).
 */
@OptIn(UnstableApi::class)
object StyledSubtitles {
    /**
     * How [track] rides beside the stream, known by [id]: the styled copy when there is one and [styled] (not offline,
     * and the title's subtitles aren't moved: the server shifts only the WebVTT copy), else WebVTT. [url] resolves the
     * server's relative addresses.
     */
    fun configuration(track: SubtitleTrack, id: String, styled: Boolean, url: (String) -> String): MediaItem.SubtitleConfiguration {
        val styledUrl = track.styledUrl?.takeIf { styled }
        return MediaItem.SubtitleConfiguration.Builder(url(styledUrl ?: track.url!!).toUri())
            .setId(id)
            .setMimeType(if (styledUrl != null) MimeTypes.TEXT_SSA else MimeTypes.TEXT_VTT)
            .setLanguage(track.language)
            .setLabel(track.label)
            .build()
    }

    /**
     * The cues as the screen draws them. A styled line with no place of its own (bottom centre, no `\pos`: Media3 puts it
     * 95% of the way down) is left to the profile's place, stacked as WebVTT's are; sizes are the profile's; drawings go.
     * WebVTT's cues pass through unchanged.
     */
    fun forScreen(cues: List<Cue>): List<Cue> {
        var stacked = 0
        return cues.filterNot { it.text?.let(::isDrawing) == true }.map { cue ->
            val unplaced = cue.lineType == Cue.LINE_TYPE_FRACTION && cue.line == DEFAULT_BOTTOM && cue.lineAnchor == Cue.ANCHOR_TYPE_END &&
                cue.position == DEFAULT_CENTRE && cue.positionAnchor == Cue.ANCHOR_TYPE_MIDDLE
            if (!unplaced && cue.textSizeType == Cue.TYPE_UNSET) return@map cue
            cue.buildUpon().apply {
                setTextSize(Cue.DIMEN_UNSET, Cue.TYPE_UNSET)
                // The first at the foot, the next above it, as WebVTT numbers its lines from the bottom.
                if (unplaced) setLine(-(++stacked).toFloat(), Cue.LINE_TYPE_NUMBER).setLineAnchor(Cue.ANCHOR_TYPE_START)
            }.build()
        }
    }

    /** An ASS vector drawing (`{\p1}m 0 0 l 100 0 …`), which Media3 would show as its numbers. */
    private fun isDrawing(text: CharSequence): Boolean = drawing.matches(text)

    private val drawing = Regex("""\s*m\s+-?\d+(\.\d+)?\s+-?\d+(\.\d+)?[\s\d.\-mnlbspc]*""")

    // Where Media3's SSA parser puts a line that doesn't say where it goes.
    private const val DEFAULT_BOTTOM = 0.95f
    private const val DEFAULT_CENTRE = 0.5f
}
