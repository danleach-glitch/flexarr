import AppIntents
import FlexarrKit
import Foundation

// Siri and Shortcuts. "Play the next episode of Curb Your Enthusiasm in Flexarr" finds the show, works out the episode
// this profile is up to and plays it: here, or on the Apple TV chosen in Settings (iPhone and iPad), or on a device
// named in the request. Podcasts the profile follows are titles too ("Play The Rest Is History in Flexarr" plays the
// episode to hear next), with intents of their own for the newest episode, what's new, and Up Next. The app model is handed to App Intents at launch (`AppDependencyManager`), so intents share
// the sign-in and the player with the rest of the app.

// MARK: - Entities

/// A film, show or followed podcast in the library, as Siri and Shortcuts see it. A podcast's id is marked
/// (`podcast:…`), so it's looked up as one: its page would carry every episode.
struct TitleEntity: AppEntity {
    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "Film, show or podcast")
    static let defaultQuery = TitleQuery()
    static let podcastPrefix = "podcast:"

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

    init(_ podcast: Podcast) {
        id = Self.podcastPrefix + podcast.id
        title = podcast.title
        year = nil
        kind = .podcast
    }

    /// The podcast's own id, for one.
    var podcastId: String? { id.hasPrefix(Self.podcastPrefix) ? String(id.dropFirst(Self.podcastPrefix.count)) : nil }

    var displayRepresentation: DisplayRepresentation {
        let what = kind == .series ? "Show" : kind == .movie ? "Film" : kind == .podcast ? "Podcast" : nil
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
            if id.hasPrefix(TitleEntity.podcastPrefix) {
                if let podcast = try? await client.podcast(String(id.dropFirst(TitleEntity.podcastPrefix.count))) { found.append(TitleEntity(podcast)) }
            } else if let detail = try? await client.item(id) {
                found.append(TitleEntity(detail.item))
            }
        }
        return found
    }

    @MainActor
    func entities(matching string: String) async throws -> [TitleEntity] {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let titles = try await client.search(string).titles.map(TitleEntity.init)
        return titles + Podcasts.matching(string, in: (try? await client.podcasts()) ?? []).map(TitleEntity.init)
    }

    /// The library's films and shows, so Siri recognises their names in a phrase. Shows first, since "play the
    /// next …" is the usual ask; capped so a big library doesn't flood the vocabulary.
    @MainActor
    func suggestedEntities() async throws -> [TitleEntity] {
        guard let client = model.client else { return [] }
        var titles: [ItemSummary] = []
        let libraries = model.libraries.isEmpty ? ((try? await client.libraries()) ?? []) : model.libraries
        for library in libraries.sorted(by: { $0.type == .tvShows && $1.type != .tvShows }) where library.type != .music && library.type != .podcasts {
            if let page = try? await client.libraryItems(library.id, sort: .added, take: 300) { titles.append(contentsOf: page.items) }
            if titles.count >= 600 { break }
        }
        var seen: Set<String> = []
        let library = titles.filter { seen.insert($0.id).inserted }.prefix(600).map(TitleEntity.init)
        return ((try? await client.podcasts()) ?? []).map(TitleEntity.init) + library
    }
}

/// A podcast this profile follows.
struct PodcastEntity: AppEntity {
    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "Podcast")
    static let defaultQuery = PodcastQuery()

    let id: String
    let title: String

    init(_ podcast: Podcast) {
        id = podcast.id
        title = podcast.title
    }

    var displayRepresentation: DisplayRepresentation { DisplayRepresentation(title: "\(title)") }
}

struct PodcastQuery: EntityStringQuery {
    @Dependency private var model: AppModel

    @MainActor
    func entities(for identifiers: [String]) async throws -> [PodcastEntity] {
        guard let client = model.client else { return [] }
        var found: [PodcastEntity] = []
        for id in identifiers {
            if let podcast = try? await client.podcast(id) { found.append(PodcastEntity(podcast)) }
        }
        return found
    }

    @MainActor
    func entities(matching string: String) async throws -> [PodcastEntity] {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        return Podcasts.matching(string, in: try await client.podcasts()).map(PodcastEntity.init)
    }

    @MainActor
    func suggestedEntities() async throws -> [PodcastEntity] {
        guard let client = model.client else { return [] }
        return ((try? await client.podcasts()) ?? []).map(PodcastEntity.init)
    }
}

/// What podcast intents share: finding a followed show by what was said, and which of its episodes to play.
@MainActor
enum Podcasts {
    /// Followed shows whose names have the words said in them, closest first ("rest is history", "history").
    static func matching(_ said: String, in podcasts: [Podcast]) -> [Podcast] {
        let words = said.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber })
        guard !words.isEmpty else { return [] }
        return podcasts.filter { podcast in
            let name = podcast.title.lowercased()
            return words.allSatisfy { name.contains($0) }
        }
        .sorted { $0.title.count < $1.title.count }
    }

    /// The episode to hear next: one part-heard, else the first unplayed in the show's order (oldest first, if it's
    /// listened to that way).
    static func nextToHear(_ podcastId: String, client: FlexarrClient) async throws -> PodcastEpisode? {
        if let started = try await client.continueListening(take: 50).first(where: { $0.podcastId == podcastId }) { return started }
        let oldest = try await client.podcast(podcastId).subscription?.oldestFirst == true
        return try await client.podcastEpisodes(podcastId, sort: oldest ? .oldest : .newest, unplayed: true, take: 1).items.first
    }

    /// The newest episode not heard yet.
    static func latest(_ podcastId: String, client: FlexarrClient) async throws -> PodcastEpisode? {
        try await client.podcastEpisodes(podcastId, sort: .newest, unplayed: true, take: 1).items.first
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
        if let id = AppModel.siriDevice, !id.isEmpty { return DeviceEntity(id: id, name: AppModel.siriDeviceName ?? "the TV", online: true) }
        #endif
        return .here
    }

    /// Plays `item` (a film, episode or track, or a show or season for its next episode) on `device`. Here, the app is
    /// opened on `flexarr://play/<id>`; elsewhere, the server is told. Returns what to say.
    static func start(_ item: ItemSummary, on device: DeviceEntity, fromBeginning: Bool = false, shuffle: Bool = false, model: AppModel) async throws -> Result {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let shuffling = shuffle && (item.kind == .series || item.kind == .season) // a film just plays
        if device.isHere {
            var playing = item
            if !shuffling, item.kind == .series || item.kind == .season {
                guard let next = try await client.nextUp(item.id) else { throw FlexarrIntentError.nothingToPlay(item.title) }
                playing = next
            }
            var components = URLComponents()
            components.scheme = "flexarr"
            components.host = "play"
            components.path = "/\(playing.id)"
            if shuffling {
                components.queryItems = [URLQueryItem(name: "shuffle", value: "1")]
            } else if fromBeginning {
                components.queryItems = [URLQueryItem(name: "t", value: "0")]
            }
            guard let url = components.url else { throw FlexarrIntentError.nothingToPlay(item.title) }
            let opened: Result = .result(opensIntent: OpenURLIntent(url), dialog: shuffling ? "Shuffling \(item.title)." : "Playing \(describe(playing)).")
            return opened
        }
        let result: DeviceCommandResult
        do {
            result = try await client.play(on: device.id, itemId: item.id, fromBeginning: fromBeginning, shuffle: shuffling)
        } catch FlexarrClientError.http(status: 404, _) {
            throw FlexarrIntentError.deviceGone(device.name)
        } catch FlexarrClientError.http(status: 409, _) {
            throw FlexarrIntentError.nothingToPlay(item.title)
        }
        let what = shuffling ? item.title : result.item.map(describe) ?? item.title
        return Result.result(dialog: result.delivered
            ? "\(shuffling ? "Shuffling" : "Playing") \(what) on \(result.deviceName)."
            : "Flexarr isn't open on \(result.deviceName). \(what) will play when you open it in the next two minutes.")
    }

    /// "The Traitors Uncloaked, season 3 episode 4", "717. The Suez Crisis, from The Rest Is History" or "Heat".
    static func describe(_ item: ItemSummary) -> String {
        if item.kind == .podcastEpisode, let show = item.seriesTitle { return "\(item.title), from \(show)" }
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
    static let description = IntentDescription("Plays a film, or the next episode of a show: here, or on another device signed in to your Flexarr server.",
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
        if let podcastId = title.podcastId {
            guard let episode = try await Podcasts.nextToHear(podcastId, client: client) else { throw FlexarrIntentError.nothingToPlay(title.title) }
            return try await Play.start(episode.summary, on: Play.target(device), model: model)
        }
        let item = try await client.item(title.id).item
        return try await Play.start(item, on: Play.target(device), fromBeginning: fromBeginning, model: model)
    }
}

/// "Play the latest The Rest Is History in Flexarr": the newest episode not heard yet.
struct PlayLatestEpisodeIntent: AppIntent {
    static let title: LocalizedStringResource = "Play a podcast's latest episode"
    static let description = IntentDescription("Plays the newest episode you haven't heard of a podcast you follow.", categoryName: "Podcasts")

    @Parameter(title: "Podcast", requestValueDialog: "Which podcast?")
    var podcast: PodcastEntity

    @Parameter(title: "On")
    var device: DeviceEntity?

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Play the latest \(\.$podcast) on \(\.$device)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog & OpensIntent {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        guard let episode = try await Podcasts.latest(podcast.id, client: client) else { throw FlexarrIntentError.nothingToPlay(podcast.title) }
        return try await Play.start(episode.summary, on: Play.target(device), model: model)
    }
}

/// "What's new in my podcasts in Flexarr": the newest episodes not heard yet, read out.
struct NewPodcastEpisodesIntent: AppIntent {
    static let title: LocalizedStringResource = "What's new in my podcasts"
    static let description = IntentDescription("Says the newest episodes you haven't heard of the podcasts you follow.", categoryName: "Podcasts")

    @Dependency private var model: AppModel

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let fresh = try await client.newPodcastEpisodes(unplayed: true, take: 5)
        guard !fresh.isEmpty else { return .result(dialog: "Nothing new. You're up to date.") }
        let said = fresh.map { "\($0.title), from \($0.podcastTitle)" }.joined(separator: ". ")
        return .result(dialog: "\(fresh.count == 1 ? "One new episode" : "\(fresh.count) new episodes"): \(said).")
    }
}

/// "Add The Rest Is History to Up Next in Flexarr": its episode to hear next goes at the end of Up Next.
struct AddToUpNextIntent: AppIntent {
    static let title: LocalizedStringResource = "Add a podcast to Up Next"
    static let description = IntentDescription("Puts the next episode of a podcast you follow at the end of Up Next.", categoryName: "Podcasts")

    @Parameter(title: "Podcast", requestValueDialog: "Which podcast?")
    var podcast: PodcastEntity

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Add \(\.$podcast) to Up Next")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        guard let episode = try await Podcasts.nextToHear(podcast.id, client: client) else { throw FlexarrIntentError.nothingToPlay(podcast.title) }
        await model.addToUpNext(episode.id, next: false)
        return .result(dialog: "Added \(episode.title) to Up Next.")
    }
}

/// "Play my Up Next in Flexarr": the first episode in Up Next (the rest follow by themselves).
struct PlayUpNextIntent: AppIntent {
    static let title: LocalizedStringResource = "Play Up Next"
    static let description = IntentDescription("Plays the podcast episodes in your Up Next, from the first.", categoryName: "Podcasts")

    @Parameter(title: "On")
    var device: DeviceEntity?

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Play Up Next on \(\.$device)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog & OpensIntent {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        guard let first = try await client.upNext().first else { throw FlexarrIntentError.nothingToPlay("Up Next") }
        return try await Play.start(first.summary, on: Play.target(device), model: model)
    }
}

/// "Shuffle Family Guy in Flexarr": a show's episodes in a random order, one after another.
struct ShuffleTitleIntent: AppIntent {
    static let title: LocalizedStringResource = "Shuffle a show"
    static let description = IntentDescription("Plays a show's episodes in a random order: here, or on another device signed in to your Flexarr server.",
                                               categoryName: "Playback")

    @Parameter(title: "Show", requestValueDialog: "Which show?")
    var title: TitleEntity

    @Parameter(title: "On")
    var device: DeviceEntity?

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Shuffle \(\.$title) on \(\.$device)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog & OpensIntent {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let item = try await client.item(title.id).item
        return try await Play.start(item, on: Play.target(device), shuffle: true, model: model)
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
            intent: ShuffleTitleIntent(),
            phrases: [
                "Shuffle \(\.$title) in \(.applicationName)",
                "Shuffle \(\.$title) with \(.applicationName)",
                "Shuffle episodes of \(\.$title) in \(.applicationName)",
                "Play \(\.$title) on shuffle in \(.applicationName)",
            ],
            shortTitle: "Shuffle a show",
            systemImageName: "shuffle"
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
            intent: PlayLatestEpisodeIntent(),
            phrases: [
                "Play the latest \(\.$podcast) in \(.applicationName)",
                "Play the latest episode of \(\.$podcast) in \(.applicationName)",
                "Play the newest \(\.$podcast) in \(.applicationName)",
            ],
            shortTitle: "Play the latest episode",
            systemImageName: "dot.radiowaves.left.and.right"
        )
        AppShortcut(
            intent: NewPodcastEpisodesIntent(),
            phrases: [
                "What's new in my podcasts in \(.applicationName)",
                "What's new in my \(.applicationName) podcasts",
                "Any new podcasts in \(.applicationName)",
            ],
            shortTitle: "What's new in podcasts",
            systemImageName: "sparkles"
        )
        AppShortcut(
            intent: AddToUpNextIntent(),
            phrases: [
                "Add \(\.$podcast) to Up Next in \(.applicationName)",
                "Add \(\.$podcast) to my Up Next in \(.applicationName)",
            ],
            shortTitle: "Add to Up Next",
            systemImageName: "text.line.last.and.arrowtriangle.forward"
        )
        AppShortcut(
            intent: PlayUpNextIntent(),
            phrases: [
                "Play my Up Next in \(.applicationName)",
                "Play Up Next in \(.applicationName)",
            ],
            shortTitle: "Play Up Next",
            systemImageName: "list.bullet"
        )
        AppShortcut(
            intent: WatchChannelIntent(),
            phrases: [
                "Put on \(\.$channel) in \(.applicationName)",
                "Watch \(\.$channel) in \(.applicationName)",
                "Switch to \(\.$channel) in \(.applicationName)",
                "Turn on \(\.$channel) in \(.applicationName)",
                "Put \(\.$channel) on in \(.applicationName)",
            ],
            shortTitle: "Put on a channel",
            systemImageName: "tv"
        )
        AppShortcut(
            intent: WhatsOnIntent(),
            phrases: [
                "What's on \(\.$channel) in \(.applicationName)",
                "What's on \(\.$channel) on \(.applicationName)",
                "What's on \(\.$channel) now in \(.applicationName)",
            ],
            shortTitle: "What's on a channel",
            systemImageName: "calendar"
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
