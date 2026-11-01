package io.github.flexarr.core

import kotlinx.serialization.Serializable
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.Request

// Audiobooks, as the server's BookDtos: a library's shelves, its books, series, authors and narrators, a book's own
// page, and what a player needs to play one (its files on one timeline). Everything here is absent from servers before
// 0.58, which have no audiobooks.

/** What a list shows about a book (on [ItemSummary.book]). Absent from servers before 0.58. */
@Serializable
data class BookSummary(
    /** Up to two authors' names, "Lee Child, Andrew Child". */
    val authors: String? = null,
    /** How many authors it has in all, for "and 2 more". */
    val authorCount: Int = 0,
    /** Its first narrator. */
    val narrator: String? = null,
    /** A series it's in, and [sequence] its number there ("3", "1.5"). */
    val series: String? = null,
    val sequence: String? = null,
    val durationSeconds: Double = 0.0,
    /** When this profile last finished it. */
    val finishedAt: String? = null,
)

@Serializable
data class BookChapter(val index: Int, val title: String, val startSeconds: Double, val endSeconds: Double)

@Serializable
data class Bookmark(val id: String, val positionSeconds: Double, val note: String? = null, val createdAt: String? = null)

/** A series a book is in, with its number there and how many books the library has of it. */
@Serializable
data class BookSeriesRef(val id: String, val name: String, val sequence: String? = null, val bookCount: Int = 0)

@Serializable(with = ChapterSource.Serializer::class)
enum class ChapterSource(override val raw: String) : ForwardCompatibleEnum {
    None("None"), Embedded("Embedded"), Cue("Cue"), Files("Files"), Even("Even"), Audible("Audible"), Edited("Edited"), Unknown("");

    object Serializer : ForwardCompatibleEnumSerializer<ChapterSource>("ChapterSource", entries, Unknown)
}

/** One of a book's credited authors, with their photo and biography for the author card. */
@Serializable
data class BookAuthor(val id: String, val name: String, val image: String? = null, val biography: String? = null) {
    /** Whether there's anything to show beyond the name. */
    val hasDetails: Boolean get() = image != null || !biography.isNullOrBlank()
}

/** A book's own page (on [ItemDetail.book]). Absent from servers before 0.58. */
@Serializable
data class BookDetail(
    val subtitle: String? = null,
    val asin: String? = null,
    val isbn: String? = null,
    val publisher: String? = null,
    val language: String? = null,
    val abridged: Boolean = false,
    val explicit: Boolean = false,
    val durationSeconds: Double = 0.0,
    val chapterSource: ChapterSource = ChapterSource.Unknown,
    val finishedAt: String? = null,
    /** This profile's speed for the book; null, its default ([BookListening.speed]). */
    val speed: Double? = null,
    val series: List<BookSeriesRef> = emptyList(),
    val chapters: List<BookChapter> = emptyList(),
    val bookmarks: List<Bookmark> = emptyList(),
    val moreInSeries: List<ItemSummary> = emptyList(),
    val byAuthor: List<ItemSummary> = emptyList(),
    val readByNarrator: List<ItemSummary> = emptyList(),
    /** The book's credited authors in order, with photos and biographies. Absent from servers before 0.58. */
    val authors: List<BookAuthor>? = null,
) {
    /** The authors worth a card: those with a photo or a biography. */
    val authorCards: List<BookAuthor> get() = authors.orEmpty().filter { it.hasDetails }
}

/**
 * One of a book's files on its timeline. [url] serves it as it is when the player said it can play its sound, and
 * converted to AAC ([converted]) when it can't.
 */
@Serializable
data class BookFile(
    val index: Int,
    val fileId: String,
    val offsetSeconds: Double,
    val durationSeconds: Double,
    val container: String? = null,
    val audioCodec: String? = null,
    val size: Long = 0,
    /** Relative to the server: a session's needs no key, an item's (for downloads) does. */
    val url: String,
    val converted: Boolean = false,
)

/** Where this profile is in a book, and which device put it there when. */
@Serializable
data class BookProgress(
    val positionSeconds: Double = 0.0,
    val finished: Boolean = false,
    val finishedAt: String? = null,
    val updatedAt: String? = null,
    val device: String? = null,
)

/** How this profile listens to books. */
@Serializable
data class BookListening(
    /** The default speed, for books without their own. */
    val speed: Double = 1.0,
    val skipBackSeconds: Int = 30,
    val skipForwardSeconds: Int = 30,
    /** After a pause, start a little earlier: more the longer the pause. */
    val smartRewind: Boolean = true,
) {
    companion object {
        /** The skip steps a profile can choose. */
        val skipChoices = listOf(10, 15, 30, 45, 60)
    }
}

/** Changes to [BookListening]; null leaves one as it is. */
@Serializable
data class BookListeningUpdate(
    val speed: Double? = null,
    val skipBackSeconds: Int? = null,
    val skipForwardSeconds: Int? = null,
    val smartRewind: Boolean? = null,
)

/**
 * Everything a player needs to play a book: its files in order on one timeline, chapters, bookmarks, where this profile
 * is, the speed to play at, and the next book in its series to offer at the end.
 */
@Serializable
data class BookManifest(
    val itemId: String,
    val durationSeconds: Double,
    val files: List<BookFile>,
    val chapters: List<BookChapter> = emptyList(),
    val bookmarks: List<Bookmark> = emptyList(),
    val progress: BookProgress = BookProgress(),
    val speed: Double = 1.0,
    val listening: BookListening = BookListening(),
    val nextInSeries: ItemSummary? = null,
)

/** A series in a library's list of them. */
@Serializable
data class BookSeriesSummary(
    val id: String,
    val name: String,
    val bookCount: Int = 0,
    val finishedCount: Int = 0,
    /** Artwork of its first few books, for a stacked cover. */
    val covers: List<Artwork> = emptyList(),
    /** The next book in it this profile hasn't finished. */
    val next: ItemSummary? = null,
)

/** A series in reading order. */
@Serializable
data class BookSeries(
    val id: String,
    val libraryId: String,
    val name: String,
    val description: String? = null,
    val books: List<BookSeriesEntry> = emptyList(),
    val nextId: String? = null,
)

@Serializable
data class BookSeriesEntry(val book: ItemSummary, val sequence: String? = null)

/** An author or narrator in a library's list of them. */
@Serializable
data class BookPerson(val id: String, val name: String, val image: String? = null, val bookCount: Int = 0)

/** An audiobooks library's front page. */
@Serializable
data class BookShelves(
    val continueListening: List<ItemSummary> = emptyList(),
    val nextInSeries: List<ItemSummary> = emptyList(),
    val recentlyAdded: List<ItemSummary> = emptyList(),
    val series: List<BookSeriesSummary> = emptyList(),
    val authors: List<BookPerson> = emptyList(),
    val narrators: List<BookPerson> = emptyList(),
    val genres: List<GenreCount> = emptyList(),
    val finished: List<ItemSummary> = emptyList(),
    /**
     * The front page's shelves in the profile's order, switched-off ones left out (see [BookShelf]). Absent from
     * servers before 0.58: every shelf, in [BookShelf]'s order.
     */
    val order: List<String>? = null,
) {
    /** The front page's shelves to show, in order: [order]'s (ids this app doesn't know left out), or all of them. */
    val frontPage: List<BookShelf>
        get() = order?.mapNotNull { id -> BookShelf.entries.firstOrNull { it.id == id } }?.distinct() ?: BookShelf.entries

    /** Whether the library has no books at all to put on a shelf. */
    val isEmpty: Boolean get() = recentlyAdded.isEmpty() && continueListening.isEmpty() && series.isEmpty()

    /** The [frontPage] shelves with something on them: none means there's no front page to show, only the books. */
    val shownShelves: List<BookShelf> get() = frontPage.filter { has(it) }

    /** Whether [shelf] has something on it. */
    fun has(shelf: BookShelf): Boolean = when (shelf) {
        BookShelf.ContinueListening -> continueListening.isNotEmpty()
        BookShelf.NextInSeries -> nextInSeries.isNotEmpty()
        BookShelf.RecentlyAdded -> recentlyAdded.isNotEmpty()
        BookShelf.Series -> series.isNotEmpty()
        BookShelf.Authors -> authors.isNotEmpty()
        BookShelf.Finished -> finished.isNotEmpty()
    }
}

/** A shelf on an audiobooks library's front page, by the id [BookShelves.order] names it with. */
enum class BookShelf(val id: String) {
    ContinueListening("continue"), NextInSeries("next"), RecentlyAdded("recent"), Series("series"), Authors("authors"), Finished("finished"),
}

/** A series a search found, with how many of its books this profile can see. */
@Serializable
data class BookSeriesSearch(val id: String, val name: String, val bookCount: Int = 0)

/** A library's books, filtered by progress. */
enum class BookProgressFilter(val raw: String, val label: String) {
    NotStarted("notstarted", "Not started"), InProgress("inprogress", "In progress"), Finished("finished", "Finished")
}

/** Orders `GET /libraries/{id}/books` takes. */
enum class BookSort(val raw: String, val label: String) {
    Title("title", "Title"),
    Author("author", "Author"),
    Series("series", "Series"),
    Added("added", "Recently added"),
    Released("released", "Release date"),
    Length("length", "Length"),
    Listened("listened", "Recently listened"),
}

enum class BookPersonRole(val raw: String) { Author("author"), Narrator("narrator") }

/** What to list of a library's books: all of them, or those matching. */
data class BookQuery(
    val search: String? = null,
    val progress: BookProgressFilter? = null,
    val authorId: String? = null,
    val narratorId: String? = null,
    val genre: String? = null,
    val language: String? = null,
    val abridged: Boolean? = null,
    val minHours: Double? = null,
    val maxHours: Double? = null,
    val sort: BookSort = BookSort.Title,
    val descending: Boolean = false,
) {
    internal fun parameters(): Map<String, String> = buildMap {
        search?.trim()?.takeIf { it.isNotEmpty() }?.let { put("search", it) }
        progress?.let { put("progress", it.raw) }
        authorId?.let { put("author", it) }
        narratorId?.let { put("narrator", it) }
        genre?.let { put("genre", it) }
        language?.let { put("language", it) }
        abridged?.let { put("abridged", it.toString()) }
        minHours?.let { put("minHours", it.toString()) }
        maxHours?.let { put("maxHours", it.toString()) }
        if (sort != BookSort.Title) put("sort", sort.raw)
        if (descending) put("descending", "true")
    }

    /** Anything narrowing the list beyond its order. */
    val isFiltered: Boolean
        get() = progress != null || authorId != null || narratorId != null || genre != null || language != null || abridged != null || minHours != null || maxHours != null
}

@Serializable
internal data class BookProgressBody(val positionSeconds: Double, val device: String? = null, val recordedAt: String? = null, val ended: Boolean = false)

@Serializable
internal data class BookFinishedBody(val finished: Boolean)

@Serializable
internal data class BookmarkBody(val positionSeconds: Double, val note: String? = null)

@Serializable
internal data class BookmarkNoteBody(val note: String?)

/** An audiobooks library's shelves. */
suspend fun FlexarrClient.bookShelves(libraryId: String): BookShelves = get("libraries/$libraryId/book-shelves")

/**
 * The library's genres, A to Z, with how many books are in each. Servers before 0.58 have no such call (they answer
 * with the web app's page, 200 text/html, or a proxy's 404): their shelves carry the genres instead.
 */
suspend fun FlexarrClient.bookGenres(libraryId: String): List<GenreCount> {
    val genres = try {
        get<List<GenreCount>>("libraries/$libraryId/book-genres")
    } catch (e: FlexarrException.Http) {
        if (e.status == 404) bookShelves(libraryId).genres else throw e
    } catch (_: FlexarrException.UnexpectedResponse) {
        bookShelves(libraryId).genres
    }
    return genres.sortedWith(compareBy(String.CASE_INSENSITIVE_ORDER) { it.name })
}

suspend fun FlexarrClient.books(libraryId: String, query: BookQuery = BookQuery(), skip: Int = 0, take: Int = 60): Paged<ItemSummary> =
    get("libraries/$libraryId/books", query.parameters() + buildMap { if (skip > 0) put("skip", "$skip"); put("take", "$take") })

suspend fun FlexarrClient.bookSeriesList(libraryId: String, search: String? = null, skip: Int = 0, take: Int = 60): Paged<BookSeriesSummary> =
    get("libraries/$libraryId/book-series", buildMap { search?.takeIf { it.isNotBlank() }?.let { put("search", it) }; if (skip > 0) put("skip", "$skip"); put("take", "$take") })

suspend fun FlexarrClient.bookPeople(libraryId: String, role: BookPersonRole, search: String? = null, skip: Int = 0, take: Int = 60): Paged<BookPerson> =
    get("libraries/$libraryId/book-people", buildMap {
        put("role", role.raw)
        search?.takeIf { it.isNotBlank() }?.let { put("search", it) }
        if (skip > 0) put("skip", "$skip")
        put("take", "$take")
    })

/** A series' books in reading order. */
suspend fun FlexarrClient.bookSeries(id: String): BookSeries = get("book-series/$id")

/**
 * A book's files with the item's own addresses (they need the sign-in), for a download. [codecs]: the sounds this
 * device decodes; files in any other come converted to AAC.
 */
suspend fun FlexarrClient.bookManifest(itemId: String, codecs: List<String>): BookManifest =
    get("items/$itemId/book", mapOf("codecs" to codecs.joinToString(",")))

/**
 * A place reached in a book without a session (a download, listened to offline): the newest [recordedAtMillis] wins on
 * the server. Returns the profile's place afterwards.
 */
suspend fun FlexarrClient.recordBookProgress(itemId: String, positionSeconds: Double, device: String?, recordedAtMillis: Long?, ended: Boolean = false): BookProgress =
    sendDecoding(
        "PUT",
        "items/$itemId/progress",
        BookProgressBody(positionSeconds.coerceAtLeast(0.0), device, recordedAtMillis?.let { java.time.Instant.ofEpochMilli(it).toString() }, ended),
    )

/**
 * Tells the server where a book's session got to. A session it has let go (404: sessions end after twelve hours idle),
 * or one already known to be gone ([sessionGone]), has the place recorded on the book instead, so it isn't lost. False
 * when the session is gone.
 */
suspend fun FlexarrClient.reportBookSession(
    sessionId: String, itemId: String, positionSeconds: Double, paused: Boolean, ended: Boolean, device: String?, sessionGone: Boolean = false,
): Boolean {
    if (!sessionGone) {
        try {
            reportProgress(sessionId, positionSeconds, paused, ended)
            return true
        } catch (e: FlexarrException.Http) {
            if (e.status != 404) throw e
        }
    }
    recordBookProgress(itemId, positionSeconds, device, System.currentTimeMillis(), ended)
    return false
}

/**
 * The end of a book's session: its last place ([ended] at the end of the book; only when [record]), then the stop, one
 * after the other, so the server has the place (and counts the book finished) before the session goes.
 */
suspend fun FlexarrClient.endBookSession(
    sessionId: String, itemId: String, positionSeconds: Double, ended: Boolean, record: Boolean, device: String?, sessionGone: Boolean = false,
) {
    var gone = sessionGone
    if (record) {
        try {
            gone = !reportBookSession(sessionId, itemId, positionSeconds, paused = true, ended = ended, device = device, sessionGone = gone)
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (_: Exception) {
        }
    }
    if (!gone) runCatching { stopPlayback(sessionId) }
}

/** Marks a book finished, or not. */
suspend fun FlexarrClient.setBookFinished(itemId: String, finished: Boolean) {
    send("POST", "items/$itemId/finished", body = FlexarrClient.json.encodeToString(BookFinishedBody.serializer(), BookFinishedBody(finished)))
}

/** Starts a book over, keeping the record that it was finished. */
suspend fun FlexarrClient.listenAgain(itemId: String) {
    send("POST", "items/$itemId/listen-again")
}

suspend fun FlexarrClient.bookmarks(itemId: String): List<Bookmark> = get("items/$itemId/bookmarks")

suspend fun FlexarrClient.addBookmark(itemId: String, positionSeconds: Double, note: String? = null): Bookmark =
    sendDecoding("POST", "items/$itemId/bookmarks", BookmarkBody(positionSeconds, note?.takeIf { it.isNotBlank() }))

suspend fun FlexarrClient.setBookmarkNote(id: String, note: String?): Bookmark =
    sendDecoding("PUT", "bookmarks/$id", BookmarkNoteBody(note?.takeIf { it.isNotBlank() }))

suspend fun FlexarrClient.deleteBookmark(id: String) {
    send("DELETE", "bookmarks/$id")
}

suspend fun FlexarrClient.bookListening(): BookListening = get("books/listening")

suspend fun FlexarrClient.setBookListening(update: BookListeningUpdate): BookListening = sendDecoding("PUT", "books/listening", update)

/** The request for one of a downloaded book's files (an item address, which needs the sign-in), from [fromByte] on. */
fun FlexarrClient.bookFileRequest(relativeUrl: String, fromByte: Long = 0): Request? {
    val url = url(relativeUrl).toHttpUrlOrNull() ?: return null
    // Only the server's own addresses carry the sign-in.
    if (url.host != baseUrl.host || url.port != baseUrl.port) return null
    return Request.Builder().url(url).signIn().apply { if (fromByte > 0) header("Range", "bytes=$fromByte-") }.build()
}

// Presentation

/** "Book 3 of 7", "Book 1.5", or null outside a series. */
fun bookNumberLine(sequence: String?, count: Int?): String? {
    val number = sequence?.takeIf { it.isNotBlank() } ?: return null
    return if (count != null && count > 1 && number.toDoubleOrNull()?.let { it == Math.floor(it) && it >= 1 } == true) "Book $number of $count" else "Book $number"
}

/** "Lee Child, Andrew Child and 2 more". */
val BookSummary.authorLine: String?
    get() {
        val names = authors?.takeIf { it.isNotBlank() } ?: return null
        val shown = names.split(", ").size
        return if (authorCount > shown) "$names and ${authorCount - shown} more" else names
    }

/** The book's length, from its summary or as a plain item. */
val ItemSummary.bookDuration: Double
    get() = book?.durationSeconds?.takeIf { it > 0 } ?: durationSeconds ?: 0.0

/** Finished by this profile. */
val ItemSummary.isBookFinished: Boolean get() = book?.finishedAt != null || (kind == MediaKind.Audiobook && userData.watched)

/** "2h 10m left at 1.5×", or the whole length when not started. [speed] counts the time it takes at that speed. */
fun bookTimeLine(duration: Double, position: Double, speed: Double, finished: Boolean): String? {
    if (duration <= 0) return null
    val rate = speed.coerceAtLeast(0.25)
    if (finished || position <= 0) return Format.runtime(duration / rate)
    val left = (duration - position).coerceAtLeast(0.0) / rate
    return Format.remaining(left) ?: "Less than a minute left"
}
