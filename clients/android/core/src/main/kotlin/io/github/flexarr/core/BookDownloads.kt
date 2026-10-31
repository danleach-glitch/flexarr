package io.github.flexarr.core

import kotlinx.serialization.Serializable

// Audiobooks kept on the phone, as the iPhone app keeps them: a whole book under one entry, its files fetched as they
// are (or as AAC where the phone can't play one) from the item's own addresses, and what's heard offline sent later with
// the time it was heard (`PUT /items/{id}/progress`, newest wins).

/** A book on this phone, whole: its manifest (files on the timeline, chapters), the files as they come, where it's at. */
@Serializable
data class LocalBook(
    val itemId: String,
    /** Whose it is: each profile sees only its own. */
    val profileId: String,
    val item: ItemSummary,
    val manifest: BookManifest,
    /** Each file's name in the downloads folder, by its index; null until it's here. */
    val fileNames: List<String?> = emptyList(),
    val phase: LocalPhase = LocalPhase.Downloading,
    val progress: Double = 0.0,
    val failure: String? = null,
    /** What the files take on the phone, once here. */
    val sizeBytes: Long = 0,
    val imageFileName: String? = null,
    /** Where listening got to, and when it was finished. */
    val position: Double = 0.0,
    val finishedAtMillis: Long? = null,
    val unsent: UnsentProgress? = null,
    /** Fetched by itself (the next in a series): it waits for Wi-Fi when Settings says so. */
    val automatic: Boolean = false,
    val createdAtMillis: Long = 0,
) {
    val isDownloaded: Boolean
        get() = phase == LocalPhase.Downloaded && manifest.files.isNotEmpty() && fileNames.size == manifest.files.size && fileNames.all { it != null }

    val isPending: Boolean get() = phase == LocalPhase.Preparing || phase == LocalPhase.Downloading

    /** About how big it is: what's here once it's all here, else the server's sizes of the files. */
    val estimatedBytes: Long get() = if (isDownloaded && sizeBytes > 0) sizeBytes else manifest.files.sumOf { it.size }

    /** Heard to [position] (or to the end): kept here, and to be told to the server. */
    fun recorded(position: Double, finished: Boolean, now: Long): LocalBook = copy(
        position = if (finished) 0.0 else position,
        finishedAtMillis = if (finished) now else if (position > 0) null else finishedAtMillis,
        unsent = UnsentProgress(position, finished, now),
    )
}

object BookDownloadRules {
    /** Finished books that can go now ([removeWhenFinished] on), with nothing left to tell the server, not playing. */
    fun finishedWith(books: List<LocalBook>, removeWhenFinished: Boolean, playing: (String) -> Boolean = { false }): List<LocalBook> =
        if (!removeWhenFinished) emptyList() else books.filter { it.finishedAtMillis != null && it.unsent == null && !playing(it.itemId) }

    /** A finished book's next in its series, to fetch by itself: not here already or on its way. */
    fun nextToFetch(book: LocalBook, books: List<LocalBook>, enabled: Boolean): ItemSummary? {
        if (!enabled || book.finishedAtMillis == null) return null
        val next = book.manifest.nextInSeries ?: return null
        return next.takeIf { candidate -> books.none { it.profileId == book.profileId && it.itemId == candidate.id } }
    }

    /** The name a book's file [index] is kept under. */
    fun fileName(itemId: String, index: Int): String = "book-${safe(itemId)}-$index.audio"

    fun imageName(itemId: String): String = "book-${safe(itemId)}.jpg"

    private fun safe(part: String) = part.replace(Regex("[^A-Za-z0-9_-]"), "_")
}
