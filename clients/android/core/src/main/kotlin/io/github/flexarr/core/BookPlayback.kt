package io.github.flexarr.core

// Playing a book, the parts that don't need a device: its files on one timeline, chapters, smart rewind, speeds, and
// when to offer to jump to where another device got to. As the server's BookPlayback and docs/audiobooks.md describe.

/** A place on a book's timeline as its files hold it: which file, and how far into it. */
data class BookFilePlace(val index: Int, val offsetSeconds: Double)

/**
 * A book's files back to back on one timeline, as the server lays them out: file n starts at its `offsetSeconds`, and a
 * timeline position belongs to the last file starting at or before it. Chapters, bookmarks and progress are all on the
 * timeline, so a player maps them to its files and back.
 */
object BookTimeline {
    /** Where [position] falls: the file, and the seconds into it (never negative). */
    fun place(files: List<BookFile>, position: Double): BookFilePlace {
        val at = position.coerceAtLeast(0.0)
        if (files.isEmpty()) return BookFilePlace(0, at)
        val index = files.indexOfLast { it.offsetSeconds <= at }.coerceAtLeast(0)
        return BookFilePlace(index, (at - files[index].offsetSeconds).coerceAtLeast(0.0))
    }

    /** The timeline position of [seconds] into file [index]. */
    fun position(files: List<BookFile>, index: Int, seconds: Double): Double =
        (files.getOrNull(index)?.offsetSeconds ?: 0.0) + seconds.coerceAtLeast(0.0)

    /** The book's whole length: the manifest's, or else the last file's end. */
    fun duration(manifest: BookManifest): Double =
        manifest.durationSeconds.takeIf { it > 0 } ?: manifest.files.lastOrNull()?.let { it.offsetSeconds + it.durationSeconds } ?: 0.0
}

/** The chapter playing at [position]: the last starting at or before it (half a second's grace), or null before the first. */
fun List<BookChapter>.chapterAt(position: Double): Int? = indexOfLast { it.startSeconds <= position + 0.5 }.takeIf { it >= 0 }

/**
 * Where Previous chapter goes from [position]: back to the start of the chapter playing, or, within its first few
 * seconds, to the one before.
 */
fun List<BookChapter>.previousChapterStart(position: Double): Double {
    val current = chapterAt(position) ?: return 0.0
    val start = this[current].startSeconds
    return if (position - start > 3 || current == 0) start else this[current - 1].startSeconds
}

/** Where Next chapter goes from [position]: the next chapter's start, or null in the last. */
fun List<BookChapter>.nextChapterStart(position: Double): Double? = getOrNull((chapterAt(position) ?: -1) + 1)?.startSeconds

/**
 * Smart rewind: how far to step back on playing again after a pause of [pausedMillis]. None under ten seconds, then
 * from two seconds growing (with the log of the pause) to thirty after an hour, and no more after that.
 */
object SmartRewind {
    const val MIN_PAUSE_SECONDS = 10.0
    const val FULL_PAUSE_SECONDS = 3600.0
    const val MIN_SECONDS = 2.0
    const val MAX_SECONDS = 30.0

    fun seconds(pausedMillis: Long): Double {
        val pause = pausedMillis / 1000.0
        if (pause < MIN_PAUSE_SECONDS) return 0.0
        val grown = Math.log(pause / MIN_PAUSE_SECONDS) / Math.log(FULL_PAUSE_SECONDS / MIN_PAUSE_SECONDS)
        return Math.round(MIN_SECONDS + (MAX_SECONDS - MIN_SECONDS) * grown.coerceIn(0.0, 1.0)).toDouble()
    }
}

/** The speeds a book plays at: 0.5× to 3.5× in 0.05 steps. */
object BookSpeed {
    const val MIN = 0.5
    const val MAX = 3.5
    const val STEP = 0.05

    /** [speed] on a step, within the range. */
    fun snapped(speed: Double): Double = Math.round(speed.coerceIn(MIN, MAX) * 20) / 20.0

    /** The quick choices offered beside the fine steps. */
    val presets = listOf(0.8, 1.0, 1.2, 1.5, 1.75, 2.0, 2.5, 3.0)

    /** "1×", "1.25×", "1.05×". */
    fun label(speed: Double): String {
        val snapped = snapped(speed)
        val text = if (snapped == Math.floor(snapped)) snapped.toInt().toString() else String.format(java.util.Locale.ROOT, "%.2f", snapped).trimEnd('0')
        return "$text×"
    }
}

/** Someone got further in this book on another device: "Jump to 3:12:04 from Pixel 9?". */
data class JumpOffer(val positionSeconds: Double, val device: String)

/**
 * Whether the server's [BookProgress] is worth offering to jump to: recorded by another device than [thisDevice], more
 * recently than this one last recorded its place ([localMillis], null when it hasn't), and somewhere else in the book
 * (half a minute or more away from [position]).
 */
fun BookProgress.jumpOffer(position: Double, localMillis: Long?, thisDevice: String): JumpOffer? {
    val device = device?.takeIf { it.isNotBlank() && !it.equals(thisDevice, ignoreCase = true) } ?: return null
    if (finished || positionSeconds <= 0) return null
    val recorded = updatedAt?.let(::parseServerTime) ?: return null
    if (localMillis != null && recorded <= localMillis) return null
    if (Math.abs(positionSeconds - position) < 30) return null
    return JumpOffer(positionSeconds, device)
}

/** A server time ("2026-10-31T09:12:00Z", or with no zone, as UTC) in milliseconds; null if it doesn't read. */
fun parseServerTime(text: String): Long? = runCatching { java.time.Instant.parse(text).toEpochMilli() }.getOrNull()
    ?: runCatching { java.time.LocalDateTime.parse(text).toInstant(java.time.ZoneOffset.UTC).toEpochMilli() }.getOrNull()
