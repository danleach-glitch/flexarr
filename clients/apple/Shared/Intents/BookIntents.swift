import AppIntents
import FlexarrKit
import Foundation

// Audiobooks in Siri and Shortcuts. The app is at Apple's limit of ten spoken shortcuts, so books share one: "Resume
// listening" picks up whichever book or podcast episode played last. Playing a named book and sleeping at the end of
// the chapter are actions in the Shortcuts app, without phrases of their own.

/// A book in the library, as Shortcuts sees it.
struct BookEntity: AppEntity {
    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "Book")
    static let defaultQuery = BookQuery()

    let id: String
    let title: String
    let authors: String?

    init(_ item: ItemSummary) {
        id = item.id
        title = item.title
        authors = item.bookAuthors
    }

    var displayRepresentation: DisplayRepresentation {
        DisplayRepresentation(title: "\(title)", subtitle: authors.map { "\($0)" })
    }
}

struct BookQuery: EntityStringQuery {
    @Dependency private var model: AppModel

    @MainActor
    func entities(for identifiers: [String]) async throws -> [BookEntity] {
        guard let client = model.client else { return [] }
        var found: [BookEntity] = []
        for id in identifiers {
            if let item = try? await client.item(id).item, item.kind == .audiobook { found.append(BookEntity(item)) }
        }
        return found
    }

    @MainActor
    func entities(matching string: String) async throws -> [BookEntity] {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        return (try await client.search(string).books ?? []).map(BookEntity.init)
    }

    /// The books being listened to, then the newest.
    @MainActor
    func suggestedEntities() async throws -> [BookEntity] {
        guard let client = model.client else { return [] }
        var books: [ItemSummary] = []
        for library in model.libraries where library.type == .audiobooks {
            if let shelves = try? await client.bookShelves(library.id) { books += shelves.continueListening + shelves.recentlyAdded }
        }
        var seen: Set<String> = []
        return books.filter { seen.insert($0.id).inserted }.map(BookEntity.init)
    }
}

/// "Resume listening in Flexarr": the book or podcast episode heard last, from where it was left.
struct ResumeListeningIntent: AudioPlaybackIntent {
    static let title: LocalizedStringResource = "Resume listening"
    static let description = IntentDescription("Picks up the book or podcast episode you listened to last.", categoryName: "Listening")

    @Dependency private var model: AppModel

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        guard model.client != nil else { throw FlexarrIntentError.notConnected }
        // Paused already: carry on.
        if let current = model.music.current, current.kind == .audiobook || current.kind == .podcastEpisode {
            if !model.music.isPlaying { model.music.resume() }
            return .result(dialog: "Resuming \(current.title).")
        }
        guard let last = await model.lastListened() else { throw FlexarrIntentError.nothingToPlay("Continue listening") }
        if last.kind == .audiobook { model.playBook(last) } else { model.playEpisode(last) }
        return .result(dialog: "Resuming \(last.title).")
    }
}

/// Plays a book from where it was left (Shortcuts).
struct PlayBookIntent: AudioPlaybackIntent {
    static let title: LocalizedStringResource = "Play a book"
    static let description = IntentDescription("Plays an audiobook from where you left it.", categoryName: "Listening")

    @Parameter(title: "Book", requestValueDialog: "Which book?")
    var book: BookEntity

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Play \(\.$book)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let item = try await client.item(book.id).item
        model.playBook(item)
        return .result(dialog: "Playing \(item.title).")
    }
}

/// Pauses the book at the end of the chapter playing (Shortcuts).
struct SleepAtChapterEndIntent: AppIntent {
    static let title: LocalizedStringResource = "Sleep at the end of the chapter"
    static let description = IntentDescription("Pauses the audiobook playing when its chapter ends.", categoryName: "Listening")

    @Dependency private var model: AppModel

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        guard model.music.isBook else { return .result(dialog: "No book is playing.") }
        model.music.sleep.set(.endOfChapter)
        return .result(dialog: "Pausing at the end of the chapter.")
    }
}
