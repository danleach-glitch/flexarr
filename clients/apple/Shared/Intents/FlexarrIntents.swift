import AppIntents
import FlexarrKit
import Foundation

// Siri and Shortcuts. "Play the next episode of Curb Your Enthusiasm in Flexarr" finds the show, works out the episode
// this profile is up to and plays it: here, or on the Apple TV chosen in Settings (iPhone and iPad), or on a device
// named in the request. The app model is handed to App Intents at launch (`AppDependencyManager`), so intents share
// the sign-in and the player with the rest of the app.

// MARK: - Entities

/// A film or show in the library, as Siri and Shortcuts see it.
struct TitleEntity: AppEntity {
    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "Film or show")
    static let defaultQuery = TitleQuery()

    let id: String
    let title: String
    let year: Int?
    let kind: MediaKind

    init(_ item: ItemSummary) {
        id = item.id
        title = item.title
        year = item.releaseYear
        kind = item.kind
    }

    var displayRepresentation: DisplayRepresentation {
        let what = kind == .series ? "Show" : kind == .movie ? "Film" : nil
        let subtitle = [year.map(String.init), what].compactMap { $0 }.joined(separator: " · ")
        return DisplayRepresentation(title: "\(title)", subtitle: subtitle.isEmpty ? nil : "\(subtitle)")
    }
}

/// Finds titles by what was said, and tells Siri the names it should listen for.
struct TitleQuery: EntityStringQuery {
    @Dependency private var model: AppModel

    @MainActor
    func entities(for identifiers: [String]) async throws -> [TitleEntity] {
        guard let client = model.client else { return [] }
        var found: [TitleEntity] = []
        for id in identifiers {
            if let detail = try? await client.item(id) { found.append(TitleEntity(detail.item)) }
        }
        return found
    }

    @MainActor
    func entities(matching string: String) async throws -> [TitleEntity] {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        return try await client.search(string).titles.map(TitleEntity.init)
    }

    /// The library's films and shows, so Siri recognises their names in a phrase. Shows first, since "play the
    /// next …" is the usual ask; capped so a big library doesn't flood the vocabulary.
    @MainActor
    func suggestedEntities() async throws -> [TitleEntity] {
        guard let client = model.client else { return [] }
        var titles: [ItemSummary] = []
        let libraries = model.libraries.isEmpty ? ((try? await client.libraries()) ?? []) : model.libraries
        for library in libraries.sorted(by: { $0.type == .tvShows && $1.type != .tvShows }) where library.type != .music {
            if let page = try? await client.libraryItems(library.id, sort: .added, take: 300) { titles.append(contentsOf: page.items) }
            if titles.count >= 600 { break }
        }
        var seen: Set<String> = []
        return titles.filter { seen.insert($0.id).inserted }.prefix(600).map(TitleEntity.init)
    }
}

/// Where to play: this device, or an Apple TV, iPhone or iPad signed in to the same server.
struct DeviceEntity: AppEntity {
    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "Device")
    static let defaultQuery = DeviceQuery()

    /// The device running the intent.
    static let hereId = "here"
    static let here = DeviceEntity(id: hereId, name: "This \(ThisDevice.name)", online: true)

    let id: String
    let name: String
    let online: Bool

    init(id: String, name: String, online: Bool) {
        self.id = id
        self.name = name
        self.online = online
    }

    init(_ device: Device) {
        self.init(id: device.id, name: device.name, online: device.online)
    }

    var isHere: Bool { id == Self.hereId }

    var displayRepresentation: DisplayRepresentation {
        DisplayRepresentation(title: "\(name)", subtitle: isHere ? nil : online ? "Flexarr is open" : "Flexarr isn't open")
    }
}

struct DeviceQuery: EntityStringQuery {
    @Dependency private var model: AppModel

    @MainActor
    func entities(for identifiers: [String]) async throws -> [DeviceEntity] {
        let all = try await suggestedEntities()
        return identifiers.compactMap { id in all.first { $0.id == id } }
    }

    @MainActor
    func entities(matching string: String) async throws -> [DeviceEntity] {
        let wanted = string.lowercased()
        return try await suggestedEntities().filter { $0.name.lowercased().contains(wanted) || (wanted.contains("here") && $0.isHere) }
    }

    @MainActor
    func suggestedEntities() async throws -> [DeviceEntity] {
        guard let client = model.client else { return [.here] }
        let others = ((try? await client.devices()) ?? []).filter { !$0.isThisOne }.map(DeviceEntity.init)
        return [.here] + others
    }
}

enum FlexarrIntentError: Error, CustomLocalizedStringResourceConvertible {
    case notConnected
    case nothingToPlay(String)
    case noSuchTitle(String)
    case deviceGone(String)

    var localizedStringResource: LocalizedStringResource {
        switch self {
        case .notConnected: "Open Flexarr and connect it to your server first."
        case .nothingToPlay(let title): "\(title) has nothing to play."
        case .noSuchTitle(let term): "Nothing in your library is called \(term)."
        case .deviceGone(let name): "\(name) isn't signed in to the server any more."
        }
    }
}

// MARK: - Playing

/// The shared part of every play intent: which device, what will play there, and the sentence Siri says.
@MainActor
enum Play {
    /// A dialog, with or without the app being opened: the one shape both ways of playing return.
    typealias Result = IntentResultContainer<Never, Never, Never, IntentDialog>

    /// The device a request plays on: the one named, else the one chosen in Settings (iPhone and iPad), else here.
    static func target(_ named: DeviceEntity?) -> DeviceEntity {
        if let named { return named }
        #if os(iOS)
        if let id = AppModel.siriDevice, !id.isEmpty { return DeviceEntity(id: id, name: AppModel.siriDeviceName ?? "the Apple TV", online: true) }
        #endif
        return .here
    }

    /// Plays `item` (a film, episode or track, or a show or season for its next episode) on `device`. Here, the app is
    /// opened on `flexarr://play/<id>`; elsewhere, the server is told. Returns what to say.
    static func start(_ item: ItemSummary, on device: DeviceEntity, fromBeginning: Bool = false, model: AppModel) async throws -> Result {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        if device.isHere {
            var playing = item
            if item.kind == .series || item.kind == .season {
                guard let next = try await client.nextUp(item.id) else { throw FlexarrIntentError.nothingToPlay(item.title) }
                playing = next
            }
            var components = URLComponents()
            components.scheme = "flexarr"
            components.host = "play"
            components.path = "/\(playing.id)"
            if fromBeginning { components.queryItems = [URLQueryItem(name: "t", value: "0")] }
            guard let url = components.url else { throw FlexarrIntentError.nothingToPlay(item.title) }
            let opened: Result = .result(opensIntent: OpenURLIntent(url), dialog: "Playing \(describe(playing)).")
            return opened
        }
        let result: DeviceCommandResult
        do {
            result = try await client.play(on: device.id, itemId: item.id, fromBeginning: fromBeginning)
        } catch FlexarrClientError.http(status: 404, _) {
            throw FlexarrIntentError.deviceGone(device.name)
        } catch FlexarrClientError.http(status: 409, _) {
            throw FlexarrIntentError.nothingToPlay(item.title)
        }
        let what = result.item.map(describe) ?? item.title
        return Result.result(dialog: result.delivered
            ? "Playing \(what) on \(result.deviceName)."
            : "Flexarr isn't open on \(result.deviceName). \(what) will play when you open it in the next two minutes.")
    }

    /// "The Traitors Uncloaked, season 3 episode 4" or "Heat".
    static func describe(_ item: ItemSummary) -> String {
        guard item.kind == .episode else { return item.title }
        let show = item.seriesTitle ?? item.title
        if let season = item.seasonNumber, let episode = item.episodeNumber { return "\(show), season \(season) episode \(episode)" }
        return "\(show), \(item.title)"
    }
}

/// "Play the next episode of Curb Your Enthusiasm in Flexarr": a show plays the episode this profile is up to, a film
/// plays (resuming where it was left).
struct PlayTitleIntent: AppIntent {
    static let title: LocalizedStringResource = "Play a film or show"
    static let description = IntentDescription("Plays a film, or the next episode of a show: here, or on another Apple TV, iPhone or iPad signed in to your Flexarr server.",
                                               categoryName: "Playback")

    @Parameter(title: "Film or show", requestValueDialog: "What would you like to watch?")
    var title: TitleEntity

    @Parameter(title: "On")
    var device: DeviceEntity?

    @Parameter(title: "From the beginning", default: false)
    var fromBeginning: Bool

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Play \(\.$title) on \(\.$device)") {
            \.$fromBeginning
        }
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog & OpensIntent {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let item = try await client.item(title.id).item
        return try await Play.start(item, on: Play.target(device), fromBeginning: fromBeginning, model: model)
    }
}

/// "Continue watching in Flexarr": the top of Continue Watching.
struct ContinueWatchingIntent: AppIntent {
    static let title: LocalizedStringResource = "Continue watching"
    static let description = IntentDescription("Picks up the film or episode you were in the middle of most recently.", categoryName: "Playback")

    @Parameter(title: "On")
    var device: DeviceEntity?

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Continue watching on \(\.$device)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog & OpensIntent {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let home = try await client.home()
        guard let item = home.rows.first(where: { $0.id == "continue" })?.items.first ?? home.featured.first else {
            throw FlexarrIntentError.nothingToPlay("Continue Watching")
        }
        return try await Play.start(item, on: Play.target(device), model: model)
    }
}

enum PlaybackAction: String, AppEnum {
    case pause, resume, stop

    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "Action")
    static let caseDisplayRepresentations: [PlaybackAction: DisplayRepresentation] = [
        .pause: "Pause", .resume: "Resume", .stop: "Stop",
    ]

    var control: DeviceControl {
        switch self {
        case .pause: .pause
        case .resume: .resume
        case .stop: .stop
        }
    }
}

/// "Pause Flexarr": pauses, resumes or stops what's playing here or on the chosen device.
struct ControlPlaybackIntent: AppIntent {
    static let title: LocalizedStringResource = "Pause, resume or stop"
    static let description = IntentDescription("Pauses, resumes or stops what Flexarr is playing, here or on another device.", categoryName: "Playback")

    @Parameter(title: "Action", default: .pause)
    var action: PlaybackAction

    @Parameter(title: "On")
    var device: DeviceEntity?

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("\(\.$action) on \(\.$device)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        let target = Play.target(device)
        if target.isHere {
            switch action {
            case .pause: model.player.pause()
            case .resume: model.player.resume()
            case .stop: model.player.closePlayer()
            }
            return .result(dialog: "Done.")
        }
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let result: DeviceCommandResult
        do {
            result = try await client.control(device: target.id, action.control)
        } catch FlexarrClientError.http(status: 404, _) {
            throw FlexarrIntentError.deviceGone(target.name)
        }
        return .result(dialog: result.delivered ? "Done." : "Flexarr isn't open on \(result.deviceName).")
    }
}

// MARK: - The system's own video intents

/// "Play Heat in Flexarr", through the system's video-playback intent (what Siri on Apple TV understands): the best
/// match for what was said plays, as `PlayTitleIntent` would.
struct FlexarrPlayVideoIntent: PlayVideoIntent {
    static let title: LocalizedStringResource = "Play a video"
    static let supportedCategories: [VideoCategory] = [.movies, .tv]

    @Parameter(title: "Title")
    var term: String

    @Dependency private var model: AppModel

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog & OpensIntent {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let spoken = term.trimmingCharacters(in: .whitespacesAndNewlines)
        let found = try await client.search(spoken)
        guard let item = found.titles.first ?? found.episodes.first else { throw FlexarrIntentError.noSuchTitle(spoken) }
        return try await Play.start(item, on: Play.target(nil), model: model)
    }
}

/// "Search for Fargo in Flexarr": opens the Search tab with the words typed in.
struct FlexarrSearchIntent: ShowInAppSearchResultsIntent {
    static let title: LocalizedStringResource = "Search Flexarr"
    static let searchScopes: [StringSearchScope] = [.general, .movies, .tv]

    @Parameter(title: "Search for")
    var criteria: StringSearchCriteria

    @Dependency private var model: AppModel

    @MainActor
    func perform() async throws -> some IntentResult {
        model.pendingSearch = criteria.term
        return .result()
    }
}

// MARK: - What Siri listens for

/// The phrases Siri answers to without any setting up, once the app is installed. Each has at most one parameter,
/// and the app's name; `updateAppShortcutParameters()` (called after signing in) teaches Siri the library's titles.
struct FlexarrShortcuts: AppShortcutsProvider {
    static let shortcutTileColor = ShortcutTileColor.navy

    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: PlayTitleIntent(),
            phrases: [
                "Play \(\.$title) in \(.applicationName)",
                "Play the next \(\.$title) in \(.applicationName)",
                "Play the next episode of \(\.$title) in \(.applicationName)",
                "Play the next \(\.$title) episode in \(.applicationName)",
                "Watch \(\.$title) in \(.applicationName)",
                "Put on \(\.$title) in \(.applicationName)",
                "Play \(\.$title) with \(.applicationName)",
                "Play something in \(.applicationName)",
            ],
            shortTitle: "Play a film or show",
            systemImageName: "play.fill"
        )
        AppShortcut(
            intent: ContinueWatchingIntent(),
            phrases: [
                "Continue watching in \(.applicationName)",
                "Continue watching with \(.applicationName)",
                "Pick up where I left off in \(.applicationName)",
                "Resume what I was watching in \(.applicationName)",
            ],
            shortTitle: "Continue watching",
            systemImageName: "play.circle"
        )
        AppShortcut(
            intent: ControlPlaybackIntent(),
            phrases: [
                "\(\.$action) \(.applicationName)",
                "\(\.$action) playback in \(.applicationName)",
            ],
            shortTitle: "Pause, resume or stop",
            systemImageName: "pause.fill"
        )
    }
}
