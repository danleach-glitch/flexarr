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
 * Where the chapter playing at [position] ends: its own end, or the next chapter's start, or the book's end ([duration]).
 * Before the first chapter, where that starts.
 */
fun List<BookChapter>.chapterEnd(position: Double, duration: Double): Double {
    val index = chapterAt(position) ?: return firstOrNull()?.startSeconds ?: duration
    val chapter = this[index]
    return chapter.endSeconds.takeIf { it > chapter.startSeconds } ?: getOrNull(index + 1)?.startSeconds ?: duration
}

/**
 * The sleep timer's End of chapter: it stops where the chapter playing ends. Set, it holds that end while the book plays
 * on (so a check that comes a moment late doesn't miss it); a seek the listener asks for (Next chapter, a chapter or
 * bookmark, the scrubber) moves it to the end of the chapter it lands in.
 */
class ChapterSleep {
    /** The place on the timeline to stop at; null when it's not set. */
    var end: Double? = null
        private set

    fun set(chapters: List<BookChapter>, position: Double, duration: Double) {
        end = chapters.chapterEnd(position, duration)
    }

    fun clear() {
        end = null
    }

    /** Gone to [position]: the end is now that chapter's. */
    fun seeked(chapters: List<BookChapter>, position: Double, duration: Double) {
        if (end != null) end = chapters.chapterEnd(position, duration)
    }

    /** Seconds of listening left at [speed] before it stops; null when it's not set. */
    fun left(position: Double, speed: Double): Double? = end?.let { (it - position) / speed.coerceAtLeast(0.5) }
}

/**
 * When a book was paused, for smart rewind on playing again. A place the listener goes to while it's paused (a chapter,
 * a bookmark, Jump to, the scrubber) forgets it, so playing starts right there rather than a little before it.
 */
class BookPause {
    var since: Long? = null
        private set

    fun paused(now: Long) {
        since = now
    }

    fun seeked() {
        since = null
    }

    /** Playing again at [now]: how long it was paused, or null (not paused, or moved since); forgotten after. */
    fun resumed(now: Long): Long? = since?.let { now - it }.also { since = null }
}

/**
 * How long a book's server session has sat idle: the server lets one go after twelve hours with no report, so a session
 * quiet for a little under that is treated as gone and a new one is asked for from the same place. Kept apart from
 * [BookPause]: a seek while paused forgets the pause (no smart rewind) but sends nothing, so the session still idles.
 */
class BookSessionClock(private val lapseMillis: Long = LAPSES_MS) {
    var lastActive: Long? = null
        private set

    /** Something reached the server, or it's playing: the session is alive at [now]. */
    fun active(now: Long) {
        lastActive = now
    }

    fun clear() {
        lastActive = null
    }

    /** Idle for longer than the server keeps a session. */
    fun lapsed(now: Long): Boolean = lastActive?.let { now - it > lapseMillis } == true

    companion object {
        /** A little under the twelve hours idle after which the server ends a session. */
        const val LAPSES_MS = 11 * 3_600_000L
    }
}

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
