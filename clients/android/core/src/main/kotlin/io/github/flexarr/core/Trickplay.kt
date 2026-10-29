package io.github.flexarr.core

import kotlinx.serialization.Serializable
import kotlinx.serialization.serializer

/**
 * How a session's scrubbing previews are laid out: thumbnails every [interval] seconds, each [width] × [height], in
 * sheets of [columns] × [rows], [count] of them over [sheets] sheets.
 */
@Serializable
data class TrickplayManifest(
    val interval: Int,
    val width: Int,
    val height: Int,
    val columns: Int,
    val rows: Int,
    val count: Int,
    val sheets: Int,
) {
    /** Where a thumbnail sits: its sheet, and its top-left corner on it in pixels. */
    data class Place(val sheet: Int, val x: Int, val y: Int)

    /** Which sheet holds the thumbnail for [seconds], and where on it. */
    fun thumbnail(seconds: Double): Place? {
        if (interval <= 0 || columns <= 0 || rows <= 0 || count <= 0 || !seconds.isFinite()) return null
        val index = (seconds.toInt() / interval).coerceIn(0, count - 1)
        val perSheet = columns * rows
        val cell = index % perSheet
        return Place(index / perSheet, (cell % columns) * width, (cell / columns) * height)
    }
}

/** This session's scrubbing previews: null until the server has made them (ask again later). */
suspend fun FlexarrClient.trickplay(sessionId: String): TrickplayManifest? =
    try {
        decodeText(serializer(), sendRaw("GET", "playback/$sessionId/trickplay.json"))
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) null else throw e
    }

/** One sheet of a session's scrubbing previews. Needs no API key. */
fun FlexarrClient.trickplaySheetUrl(sessionId: String, sheet: Int): String = url("api/v1/playback/$sessionId/trickplay/$sheet.jpg")
