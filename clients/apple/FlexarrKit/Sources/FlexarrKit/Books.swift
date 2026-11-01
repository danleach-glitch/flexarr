import Foundation

// Audiobooks: a library type of their own, each book one timeline however many files it has (docs/audiobooks.md).
// Everything here is absent from servers before 0.58.

// MARK: - Models

/// What a list shows about a book (`ItemSummary.book`).
public struct BookSummary: Codable, Sendable, Hashable {
    /// Up to two authors' names, "Lee Child, Andrew Child".
    public let authors: String?
    /// How many authors it has in all, for "and 2 more".
    public let authorCount: Int
    public let narrator: String?
    /// A series it's in, and its number there ("3", "1.5").
    public let series: String?
    public let sequence: String?
    public let durationSeconds: Double
    /// When this profile last finished it.
    public let finishedAt: Date?

    public init(authors: String?, authorCount: Int, narrator: String?, series: String?, sequence: String?, durationSeconds: Double, finishedAt: Date?) {
        self.authors = authors
        self.authorCount = authorCount
        self.narrator = narrator
        self.series = series
        self.sequence = sequence
        self.durationSeconds = durationSeconds
        self.finishedAt = finishedAt
    }
}

/// Where a book's chapters came from.
public enum ChapterSource: String, ForwardCompatibleEnum {
    case none = "None", embedded = "Embedded", cue = "Cue", files = "Files", even = "Even", unknown
}

public struct BookChapter: Codable, Sendable, Hashable, Identifiable {
    public let index: Int
    public let title: String
    public let startSeconds: Double
    public let endSeconds: Double

    public var id: Int { index }
    public var durationSeconds: Double { max(0, endSeconds - startSeconds) }

    public init(index: Int, title: String, startSeconds: Double, endSeconds: Double) {
        self.index = index
        self.title = title
        self.startSeconds = startSeconds
        self.endSeconds = endSeconds
    }
}

public struct Bookmark: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    /// On the whole book's timeline.
    public let positionSeconds: Double
    public let note: String?
    public let createdAt: Date

    public init(id: String, positionSeconds: Double, note: String?, createdAt: Date) {
        self.id = id
        self.positionSeconds = positionSeconds
        self.note = note
        self.createdAt = createdAt
    }
}

/// A series a book is in, with its number there and how many books the library has of it.
public struct BookSeriesRef: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let name: String
    public let sequence: String?
    public let bookCount: Int

    /// "Book 3 of 7", "Book 1.5", or nil without a number.
    public var placeLabel: String? {
        guard let sequence, !sequence.isEmpty else { return nil }
        return bookCount > 1 && Double(sequence).map({ $0 <= Double(bookCount) }) == true ? "Book \(sequence) of \(bookCount)" : "Book \(sequence)"
    }
}

/// A book's own page (`ItemDetail.book`).
public struct BookDetail: Codable, Sendable {
    public let subtitle: String?
    public let asin: String?
    public let isbn: String?
    public let publisher: String?
    public let language: String?
    public let abridged: Bool
    public let explicit: Bool
    public let durationSeconds: Double
    public let chapterSource: ChapterSource
    public let finishedAt: Date?
    /// This profile's speed for the book; nil, its default (`BookListening.speed`).
    public let speed: Double?
    public let series: [BookSeriesRef]
    public let chapters: [BookChapter]
    public let bookmarks: [Bookmark]
    public let moreInSeries: [ItemSummary]
    public let byAuthor: [ItemSummary]
    public let readByNarrator: [ItemSummary]
}

/// One of a book's files on its timeline. `url` serves it as it is, or converted to AAC (`converted`) when the
/// player said it can't decode its sound.
public struct BookFile: Codable, Sendable, Hashable {
    public let index: Int
    public let fileId: String
    public let offsetSeconds: Double
    public let durationSeconds: Double
    public let container: String?
    public let audioCodec: String?
    public let size: Int64
    /// Relative to the server. A session's needs no key; an item's (`GET /items/{id}/book`) does.
    public let url: String
    public let converted: Bool

    public init(index: Int, fileId: String, offsetSeconds: Double, durationSeconds: Double, container: String? = nil, audioCodec: String? = nil,
                size: Int64 = 0, url: String = "", converted: Bool = false) {
        self.index = index
        self.fileId = fileId
        self.offsetSeconds = offsetSeconds
        self.durationSeconds = durationSeconds
        self.container = container
        self.audioCodec = audioCodec
        self.size = size
        self.url = url
        self.converted = converted
    }
}

/// Where this profile is in a book.
public struct BookProgress: Codable, Sendable, Hashable {
    /// On the whole book's timeline.
    public let positionSeconds: Double
    public let finished: Bool
    public let finishedAt: Date?
    /// When a device recorded it.
    public let updatedAt: Date?
    /// The device that recorded it ("Dan's iPhone").
    public let device: String?

    public init(positionSeconds: Double, finished: Bool, finishedAt: Date? = nil, updatedAt: Date? = nil, device: String? = nil) {
        self.positionSeconds = positionSeconds
        self.finished = finished
        self.finishedAt = finishedAt
        self.updatedAt = updatedAt
        self.device = device
    }
}

/// How this profile listens to books.
public struct BookListening: Codable, Sendable, Hashable {
    /// The default speed, for books without their own.
    public var speed: Double
    public var skipBackSeconds: Int
    public var skipForwardSeconds: Int
    /// After a pause, start a little earlier: more the longer the pause.
    public var smartRewind: Bool

    public init(speed: Double = 1, skipBackSeconds: Int = 30, skipForwardSeconds: Int = 30, smartRewind: Bool = true) {
        self.speed = speed
        self.skipBackSeconds = skipBackSeconds
        self.skipForwardSeconds = skipForwardSeconds
        self.smartRewind = smartRewind
    }

    /// The skip steps the server takes.
    public static let skipSteps = [10, 15, 30, 45, 60]
}

/// Changes to `BookListening`; nil keeps what the profile has.
public struct BookListeningUpdate: Codable, Sendable, Hashable {
    public var speed: Double?
    public var skipBackSeconds: Int?
    public var skipForwardSeconds: Int?
    public var smartRewind: Bool?

    public init(speed: Double? = nil, skipBackSeconds: Int? = nil, skipForwardSeconds: Int? = nil, smartRewind: Bool? = nil) {
        self.speed = speed
        self.skipBackSeconds = skipBackSeconds
        self.skipForwardSeconds = skipForwardSeconds
        self.smartRewind = smartRewind
    }
}

/// Everything a player needs to play a book: its files on one timeline, chapters, bookmarks, where this profile is,
/// the speed to play at, and the next book in its series to offer at the end.
public struct BookManifest: Codable, Sendable, Hashable {
    public let itemId: String
    public let durationSeconds: Double
    public let files: [BookFile]
    public let chapters: [BookChapter]
    public let bookmarks: [Bookmark]
    public let progress: BookProgress
    public let speed: Double
    public let listening: BookListening
    public let nextInSeries: ItemSummary?

    public init(itemId: String, durationSeconds: Double, files: [BookFile], chapters: [BookChapter], bookmarks: [Bookmark] = [],
                progress: BookProgress = BookProgress(positionSeconds: 0, finished: false), speed: Double = 1,
                listening: BookListening = BookListening(), nextInSeries: ItemSummary? = nil) {
        self.itemId = itemId
        self.durationSeconds = durationSeconds
        self.files = files
        self.chapters = chapters
        self.bookmarks = bookmarks
        self.progress = progress
        self.speed = speed
        self.listening = listening
        self.nextInSeries = nextInSeries
    }

    public var timeline: BookTimeline { BookTimeline(files: files, chapters: chapters, duration: durationSeconds) }

    /// A downloaded book's manifest brought up to date from `fresh` (the server's now): the speed, bookmarks, how
    /// this profile listens, where it is and the series' next book come from the server; the files, chapters and
    /// length stay these, which describe the files on the device. `speed`, when given, wins over the server's (a
    /// change made offline that the server hasn't been told yet).
    public func refreshed(from fresh: BookManifest, speed: Double? = nil) -> BookManifest {
        guard fresh.itemId == itemId else { return self }
        return BookManifest(itemId: itemId, durationSeconds: durationSeconds, files: files, chapters: chapters, bookmarks: fresh.bookmarks,
                            progress: fresh.progress, speed: speed ?? fresh.speed, listening: fresh.listening, nextInSeries: fresh.nextInSeries)
    }

    /// The same manifest at another speed.
    public func withSpeed(_ speed: Double) -> BookManifest {
        BookManifest(itemId: itemId, durationSeconds: durationSeconds, files: files, chapters: chapters, bookmarks: bookmarks,
                     progress: progress, speed: speed, listening: listening, nextInSeries: nextInSeries)
    }
}

/// A place a device reached in a book, for `PUT /items/{id}/progress` (offline listening sent later with its time).
public struct BookProgressUpdate: Codable, Sendable, Hashable {
    public var positionSeconds: Double
    public var device: String?
    public var recordedAt: Date?
    public var ended: Bool

    public init(positionSeconds: Double, device: String? = nil, recordedAt: Date? = nil, ended: Bool = false) {
        self.positionSeconds = positionSeconds
        self.device = device
        self.recordedAt = recordedAt
        self.ended = ended
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(max(0, positionSeconds), forKey: .positionSeconds)
        try container.encodeIfPresent(device, forKey: .device)
        try container.encodeIfPresent(recordedAt.map { $0.formatted(.iso8601) }, forKey: .recordedAt)
        try container.encode(ended, forKey: .ended)
    }
}

/// A series in a library's list of them.
public struct BookSeriesSummary: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let name: String
    public let bookCount: Int
    public let finishedCount: Int
    /// Artwork of its first few books, for a stacked cover.
    public let covers: [Artwork]
    /// The next book in it this profile hasn't finished.
    public let next: ItemSummary?
}

public struct BookSeriesEntry: Codable, Sendable, Hashable, Identifiable {
    public let book: ItemSummary
    public let sequence: String?

    public var id: String { book.id }
}

/// A series in reading order.
public struct BookSeries: Codable, Sendable, Identifiable {
    public let id: String
    public let libraryId: String
    public let name: String
    public let description: String?
    public let books: [BookSeriesEntry]
    /// The next book this profile hasn't finished.
    public let nextId: String?
}

/// An author or narrator in a library's list of them.
public struct BookPerson: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let name: String
    public let image: String?
    public let bookCount: Int
}

/// An audiobooks library's front page.
public struct BookShelves: Codable, Sendable {
    public let continueListening: [ItemSummary]
    public let nextInSeries: [ItemSummary]
    public let recentlyAdded: [ItemSummary]
    public let series: [BookSeriesSummary]
    public let authors: [BookPerson]
    public let narrators: [BookPerson]
    public let genres: [GenreCount]
    public let finished: [ItemSummary]

    public var isEmpty: Bool {
        continueListening.isEmpty && nextInSeries.isEmpty && recentlyAdded.isEmpty && series.isEmpty && authors.isEmpty && finished.isEmpty
    }
}

/// A series a search found.
public struct BookSeriesSearchResult: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let name: String
    public let bookCount: Int
}

// MARK: - Browsing

public enum BookProgressFilter: String, CaseIterable, Sendable, Identifiable {
    case any = "", notStarted = "notstarted", inProgress = "inprogress", finished
    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .any: "All"
        case .notStarted: "Not started"
        case .inProgress: "In progress"
        case .finished: "Finished"
        }
    }
}

public enum BookSort: String, CaseIterable, Sendable, Identifiable {
    case title, author, series, added, released, length, listened
    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .title: "Title"
        case .author: "Author"
        case .series: "Series"
        case .added: "Recently added"
        case .released: "Release date"
        case .length: "Length"
        case .listened: "Last listened"
        }
    }

    /// Newest, longest and latest first; names A to Z.
    public var descendingByDefault: Bool { self == .added || self == .released || self == .length || self == .listened }
}

/// What `GET /libraries/{id}/books` narrows to.
public struct BookFilter: Sendable, Hashable {
    public var search: String?
    public var progress: BookProgressFilter = .any
    public var authorId: String?
    public var narratorId: String?
    public var genre: String?
    public var language: String?
    public var abridged: Bool?
    public var minHours: Double?
    public var maxHours: Double?
    public var sort: BookSort = .title
    public var descending: Bool?

    public init(search: String? = nil, progress: BookProgressFilter = .any, authorId: String? = nil, narratorId: String? = nil, genre: String? = nil,
                language: String? = nil, abridged: Bool? = nil, minHours: Double? = nil, maxHours: Double? = nil, sort: BookSort = .title,
                descending: Bool? = nil) {
        self.search = search
        self.progress = progress
        self.authorId = authorId
        self.narratorId = narratorId
        self.genre = genre
        self.language = language
        self.abridged = abridged
        self.minHours = minHours
        self.maxHours = maxHours
        self.sort = sort
        self.descending = descending
    }

    var query: [URLQueryItem] {
        var query: [URLQueryItem] = []
        func add(_ name: String, _ value: String?) {
            if let value, !value.isEmpty { query.append(.init(name: name, value: value)) }
        }
        add("search", search?.trimmingCharacters(in: .whitespaces))
        add("progress", progress.rawValue)
        add("author", authorId)
        add("narrator", narratorId)
        add("genre", genre)
        add("language", language)
        add("abridged", abridged.map { $0 ? "true" : "false" })
        add("minHours", minHours.map { String($0) })
        add("maxHours", maxHours.map { String($0) })
        add("sort", sort.rawValue)
        add("descending", (descending ?? sort.descendingByDefault) ? "true" : "false")
        return query
    }
}

public enum BookPersonRole: String, Sendable {
    case author, narrator
}

// MARK: - The timeline

/// A book's files back to back on one timeline: where a place in the book is in its files, and which chapter it's in.
public struct BookTimeline: Sendable, Hashable {
    public let files: [BookFile]
    public let chapters: [BookChapter]
    public let duration: Double

    public init(files: [BookFile], chapters: [BookChapter] = [], duration: Double? = nil) {
        self.files = files.sorted { $0.index < $1.index }
        self.chapters = chapters.sorted { $0.startSeconds < $1.startSeconds }
        self.duration = duration ?? files.reduce(0) { max($0, $1.offsetSeconds + $1.durationSeconds) }
    }

    /// The file playing `position` seconds into the book (the last whose offset is at or before it) and how far into
    /// that file it is. Before the start is the first file's start; past the end, the last file's end.
    public func locate(_ position: Double) -> (file: Int, offset: Double) {
        guard !files.isEmpty else { return (0, 0) }
        let place = position.isFinite ? max(0, position) : 0
        var found = 0
        for (index, file) in files.enumerated() where file.offsetSeconds <= place {
            found = index
        }
        let file = files[found]
        let offset = place - file.offsetSeconds
        return (found, file.durationSeconds > 0 ? min(offset, file.durationSeconds) : max(0, offset))
    }

    /// The place in the book of `offset` seconds into file `file` (its index in `files`).
    public func position(file: Int, offset: Double) -> Double {
        guard files.indices.contains(file) else { return 0 }
        return min(duration, files[file].offsetSeconds + max(0, offset))
    }

    /// The chapter `position` is in: the last that starts at or before it.
    public func chapterIndex(at position: Double) -> Int? {
        guard !chapters.isEmpty else { return nil }
        var found: Int?
        for (index, chapter) in chapters.enumerated() where chapter.startSeconds <= position + 0.001 {
            found = index
        }
        return found ?? 0
    }

    public func chapter(at position: Double) -> BookChapter? {
        chapterIndex(at: position).map { chapters[$0] }
    }

    /// Where Previous goes: the start of this chapter, or of the one before when it's only just begun (3 seconds).
    public func previousChapterStart(from position: Double) -> Double {
        guard let index = chapterIndex(at: position) else { return 0 }
        if position - chapters[index].startSeconds > 3 || index == 0 { return chapters[index].startSeconds }
        return chapters[index - 1].startSeconds
    }

    /// Where Next goes: the start of the next chapter, or nil in the last.
    public func nextChapterStart(from position: Double) -> Double? {
        guard let index = chapterIndex(at: position), index + 1 < chapters.count else { return nil }
        return chapters[index + 1].startSeconds
    }

    /// The end of the chapter `position` is in (the book's end without chapters).
    public func chapterEnd(at position: Double) -> Double {
        chapter(at: position)?.endSeconds ?? duration
    }
}

/// How far back a book starts again after a pause: nothing under 10 seconds, then a few seconds growing to about 30
/// after an hour.
public enum SmartRewind {
    public static func seconds(afterPause pause: TimeInterval) -> Double {
        guard pause.isFinite, pause >= 10 else { return 0 }
        let grown = 3 + 27 * log(pause / 10) / log(360)
        return min(30, max(3, grown)).rounded()
    }
}

/// The speeds a book plays at: 0.5× to 3.5× in 0.05 steps.
public enum BookSpeed {
    public static let range: ClosedRange<Double> = 0.5...3.5
    public static let step = 0.05

    public static func clamped(_ speed: Double) -> Double {
        guard speed.isFinite else { return 1 }
        return (min(range.upperBound, max(range.lowerBound, speed)) / step).rounded() * step
    }

    /// "1.25×", "2×".
    public static func label(_ speed: Double) -> String {
        String(format: "%g×", (speed * 100).rounded() / 100)
    }
}

/// Whether the place the server has for a book is worth offering to jump to: newer than this device's own, from
/// another device, and more than half a minute away.
public enum BookResume {
    public static func offersJump(server: BookProgress, local: Double, localAt: Date?, device: String) -> Bool {
        guard !server.finished, let at = server.updatedAt, let from = server.device, from != device else { return false }
        if let localAt, localAt >= at { return false }
        return abs(server.positionSeconds - local) > 30
    }
}

extension ItemSummary {
    /// An audiobook: its own screens and the player's book mode.
    public var isBook: Bool { kind == .audiobook }

    /// "Lee Child", "Lee Child and 2 more".
    public var bookAuthors: String? {
        guard let book, let authors = book.authors, !authors.isEmpty else { return nil }
        let named = authors.split(separator: ",").count
        return book.authorCount > named ? "\(authors) and \(book.authorCount - named) more" : authors
    }
}

// MARK: - Client

extension FlexarrClient {
    public func bookShelves(_ libraryId: String) async throws -> BookShelves {
        try await get("libraries/\(libraryId)/book-shelves")
    }

    public func books(_ libraryId: String, filter: BookFilter = BookFilter(), skip: Int = 0, take: Int = 60) async throws -> Paged<ItemSummary> {
        var query = filter.query
        if skip > 0 { query.append(.init(name: "skip", value: String(skip))) }
        query.append(.init(name: "take", value: String(take)))
        return try await get("libraries/\(libraryId)/books", query: query)
    }

    public func bookSeriesList(_ libraryId: String, search: String? = nil, skip: Int = 0, take: Int = 60) async throws -> Paged<BookSeriesSummary> {
        var query: [URLQueryItem] = [.init(name: "take", value: String(take))]
        if let search, !search.isEmpty { query.append(.init(name: "search", value: search)) }
        if skip > 0 { query.append(.init(name: "skip", value: String(skip))) }
        return try await get("libraries/\(libraryId)/book-series", query: query)
    }

    public func bookPeople(_ libraryId: String, role: BookPersonRole, search: String? = nil, skip: Int = 0, take: Int = 60) async throws -> Paged<BookPerson> {
        var query: [URLQueryItem] = [.init(name: "role", value: role.rawValue), .init(name: "take", value: String(take))]
        if let search, !search.isEmpty { query.append(.init(name: "search", value: search)) }
        if skip > 0 { query.append(.init(name: "skip", value: String(skip))) }
        return try await get("libraries/\(libraryId)/book-people", query: query)
    }

    public func bookSeries(_ id: String) async throws -> BookSeries {
        try await get("book-series/\(id)")
    }

    /// The book's files with the item's own addresses (which need the key), for downloading. `codecs`: the sounds
    /// this device decodes; files in any other come converted to AAC.
    public func bookManifest(_ itemId: String, codecs: [String]) async throws -> BookManifest {
        try await get("items/\(itemId)/book", query: [.init(name: "codecs", value: codecs.joined(separator: ","))])
    }

    /// The request for one of a book's files, signed in, for a background download session. Only the server's own
    /// address carries the sign-in: a file elsewhere (an absolute address in the manifest) gets no request at all.
    public func bookFileRequest(_ file: BookFile) -> URLRequest? {
        guard let url = url(for: file.url), isServer(url) else { return nil }
        var request = URLRequest(url: url)
        if let apiKey, !apiKey.isEmpty { request.setValue(apiKey, forHTTPHeaderField: "X-Api-Key") }
        if let profileToken, !profileToken.isEmpty { request.setValue(profileToken, forHTTPHeaderField: "X-Flexarr-Profile") }
        if let deviceType = Self.deviceType { request.setValue(deviceType, forHTTPHeaderField: "X-Flexarr-Device") }
        return request
    }

    /// Whether `url` is on this server: the same host and port as `baseURL` (the scheme's port when none is written).
    func isServer(_ url: URL) -> Bool {
        func port(_ url: URL) -> Int? {
            url.port ?? (url.scheme?.lowercased() == "https" ? 443 : url.scheme?.lowercased() == "http" ? 80 : nil)
        }
        guard let host = url.host()?.lowercased(), host == baseURL.host()?.lowercased() else { return false }
        return port(url) == port(baseURL)
    }

    /// A place reached without a session (offline, from a download); the newest `recordedAt` wins on the server.
    @discardableResult
    public func reportBookProgress(itemId: String, _ update: BookProgressUpdate) async throws -> BookProgress {
        let data = try await send("PUT", "items/\(itemId)/progress", body: update)
        return try decode(BookProgress.self, from: data)
    }

    public func setBookFinished(_ itemId: String, finished: Bool) async throws {
        _ = try await send("POST", "items/\(itemId)/finished", body: FinishedBody(finished: finished))
    }

    /// Starts the book over, keeping when it was finished.
    public func listenAgain(_ itemId: String) async throws {
        _ = try await send("POST", "items/\(itemId)/listen-again")
    }

    public func bookmarks(_ itemId: String) async throws -> [Bookmark] {
        try await get("items/\(itemId)/bookmarks")
    }

    public func addBookmark(_ itemId: String, positionSeconds: Double, note: String? = nil) async throws -> Bookmark {
        let data = try await send("POST", "items/\(itemId)/bookmarks", body: BookmarkBody(positionSeconds: max(0, positionSeconds), note: note))
        return try decode(Bookmark.self, from: data)
    }

    public func setBookmarkNote(_ id: String, note: String?) async throws -> Bookmark {
        let data = try await send("PUT", "bookmarks/\(id)", body: BookmarkNoteBody(note: note))
        return try decode(Bookmark.self, from: data)
    }

    public func deleteBookmark(_ id: String) async throws {
        _ = try await send("DELETE", "bookmarks/\(id)")
    }

    public func bookListening() async throws -> BookListening {
        try await get("books/listening")
    }

    public func setBookListening(_ update: BookListeningUpdate) async throws -> BookListening {
        let data = try await send("PUT", "books/listening", body: update)
        return try decode(BookListening.self, from: data)
    }

    private struct FinishedBody: Encodable { let finished: Bool }
    private struct BookmarkBody: Encodable {
        let positionSeconds: Double
        let note: String?
    }
    private struct BookmarkNoteBody: Encodable { let note: String? }
}
