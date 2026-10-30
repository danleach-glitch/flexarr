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

/// Someone in the household, to play as: Continue Watching and the next episode are theirs.
struct ProfileEntity: AppEntity {
    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "Profile")
    static let defaultQuery = ProfileQuery()

    let id: String
    let name: String
    let asksPin: Bool

    init(_ profile: Profile) {
        id = profile.id
        name = profile.name
        asksPin = profile.needsPinToChoose
    }

    var displayRepresentation: DisplayRepresentation {
        DisplayRepresentation(title: "\(name)", subtitle: asksPin ? "Asks for the PIN" : nil)
    }
}

struct ProfileQuery: EntityStringQuery {
    @Dependency private var model: AppModel

    @MainActor
    func entities(for identifiers: [String]) async throws -> [ProfileEntity] {
        try await suggestedEntities().filter { identifiers.contains($0.id) }
    }

    @MainActor
    func entities(matching string: String) async throws -> [ProfileEntity] {
        let wanted = string.lowercased()
        return try await suggestedEntities().filter { $0.name.lowercased().contains(wanted) }
    }

    @MainActor
    func suggestedEntities() async throws -> [ProfileEntity] {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let profiles = model.profiles.isEmpty ? try await client.profiles() : model.profiles
        return profiles.map(ProfileEntity.init)
    }
}

enum FlexarrIntentError: Error, CustomLocalizedStringResourceConvertible {
    case notConnected
    case nothingToPlay(String)
    case noSuchTitle(String)
    case deviceGone(String)
    case noWatchlist

    var localizedStringResource: LocalizedStringResource {
        switch self {
        case .noWatchlist: "Your Flexarr server needs updating for the watchlist."
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

    /// Plays `item` (a film, episode or track, or a show or season for its next episode) on `device`, as `profile` when
    /// one is given (the device switches to it first, asking for its PIN if it has one). Here, the app is opened on
    /// `flexarr://play/<id>`; elsewhere, the server is told. Returns what to say.
    static func start(_ item: ItemSummary, on device: DeviceEntity, as profile: ProfileEntity? = nil, fromBeginning: Bool = false, shuffle: Bool = false,
                      model: AppModel) async throws -> Result {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let shuffling = shuffle && (item.kind == .series || item.kind == .season) // a film just plays
        if device.isHere {
            // Someone else's next episode is worked out once the app has switched to them.
            let switching = profile.flatMap { $0.id == model.currentProfile?.id ? nil : $0 }
            var playing = item
            if !shuffling, switching == nil, item.kind == .series || item.kind == .season {
                guard let next = try await client.nextUp(item.id) else { throw FlexarrIntentError.nothingToPlay(item.title) }
                playing = next
            }
            var components = URLComponents()
            components.scheme = "flexarr"
            components.host = "play"
            components.path = "/\(playing.id)"
            var query: [URLQueryItem] = []
            if shuffling {
                query.append(URLQueryItem(name: "shuffle", value: "1"))
            } else if fromBeginning {
                query.append(URLQueryItem(name: "t", value: "0"))
            }
            if let switching { query.append(URLQueryItem(name: "profile", value: switching.id)) }
            components.queryItems = query.isEmpty ? nil : query
            guard let url = components.url else { throw FlexarrIntentError.nothingToPlay(item.title) }
            let what = playing.id == item.id && (item.kind == .series || item.kind == .season) && !shuffling ? "the next \(item.title)" : describe(playing)
            let said = shuffling ? "Shuffling \(item.title)" : "Playing \(what)"
            let opened: Result = .result(opensIntent: OpenURLIntent(url), dialog: "\(said)\(switching.map { " as \($0.name)" } ?? "").")
            return opened
        }
        let result: DeviceCommandResult
        do {
            result = try await client.play(on: device.id, itemId: item.id, fromBeginning: fromBeginning, shuffle: shuffling, profileId: profile?.id)
        } catch FlexarrClientError.http(status: 404, _) {
            throw FlexarrIntentError.deviceGone(device.name)
        } catch FlexarrClientError.http(status: 409, _) {
            throw FlexarrIntentError.nothingToPlay(item.title)
        }
        let what = shuffling ? item.title : result.item.map(describe) ?? item.title
        let who = profile.map { " as \($0.name)" } ?? ""
        return Result.result(dialog: result.delivered
            ? "\(shuffling ? "Shuffling" : "Playing") \(what) on \(result.deviceName)\(who)."
            : "Flexarr isn't open on \(result.deviceName). \(what) will play\(who) when you open it in the next two minutes.")
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

    @Parameter(title: "As", description: "The profile to play it as: its next episode, and its Continue Watching. It asks for the PIN if the profile has one.")
    var profile: ProfileEntity?

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Play \(\.$title) on \(\.$device)") {
            \.$profile
            \.$fromBeginning
        }
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog & OpensIntent {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        if let podcastId = title.podcastId {
            guard let episode = try await Podcasts.nextToHear(podcastId, client: client) else { throw FlexarrIntentError.nothingToPlay(title.title) }
            return try await Play.start(episode.summary, on: Play.target(device), as: profile, model: model)
        }
        let item = try await client.item(title.id).item
        return try await Play.start(item, on: Play.target(device), as: profile, fromBeginning: fromBeginning, model: model)
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

/// "Add Severance to my watchlist in Flexarr": a film or show from the library, for this profile to watch later.
struct AddToWatchlistIntent: AppIntent {
    static let title: LocalizedStringResource = "Add to watchlist"
    static let description = IntentDescription("Puts a film or show from your library on your watchlist, to watch later.", categoryName: "Watchlist")

    @Parameter(title: "Film or show", requestValueDialog: "What would you like to add?")
    var title: TitleEntity

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Add \(\.$title) to my watchlist")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        guard title.kind == .movie || title.kind == .series else { throw FlexarrIntentError.noSuchTitle(title.title) }
        if !model.hasWatchlist { await model.refreshWatchlist() }
        guard model.hasWatchlist else { throw FlexarrIntentError.noWatchlist }
        do {
            try await client.addToWatchlist(.library(title.id))
        } catch FlexarrClientError.http(status: 404, _) {
            throw FlexarrIntentError.noSuchTitle(title.title)
        }
        model.invalidate()
        await model.refreshWatchlist()
        return .result(dialog: "Added \(title.title) to your watchlist.")
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

    @Parameter(title: "As", description: "The profile to watch as. It asks for the PIN if the profile has one.")
    var profile: ProfileEntity?

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Shuffle \(\.$title) on \(\.$device)") {
            \.$profile
        }
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog & OpensIntent {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let item = try await client.item(title.id).item
        return try await Play.start(item, on: Play.target(device), as: profile, shuffle: true, model: model)
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
    case pause, resume, stop, skipIntro, next, back, forward, subtitlesOn, subtitlesOff

    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "Action")
    static let caseDisplayRepresentations: [PlaybackAction: DisplayRepresentation] = [
        .pause: "Pause", .resume: "Resume", .stop: "Stop",
        .skipIntro: DisplayRepresentation(title: "Skip intro", synonyms: ["Skip the intro", "Skip recap", "Skip the recap"]),
        .next: DisplayRepresentation(title: "Next episode", synonyms: ["Play the next episode", "Skip to the next episode"]),
        .back: DisplayRepresentation(title: "Skip back", synonyms: ["Go back", "Rewind"]),
        .forward: DisplayRepresentation(title: "Skip forward", synonyms: ["Skip ahead", "Fast forward"]),
        .subtitlesOn: DisplayRepresentation(title: "Turn on subtitles", synonyms: ["Subtitles on", "Show subtitles"]),
        .subtitlesOff: DisplayRepresentation(title: "Turn off subtitles", synonyms: ["Subtitles off", "Hide subtitles"]),
    ]

    /// The command it is, as another device takes it.
    var kind: DeviceCommandKind {
        switch self {
        case .pause: .pause
        case .resume: .resume
        case .stop: .stop
        case .skipIntro: .skipSegment
        case .next: .next
        case .back, .forward: .skip
        case .subtitlesOn, .subtitlesOff: .subtitleTrack
        }
    }
}

/// "Pause Flexarr", "Skip intro in Flexarr", "Next episode in Flexarr", "Skip back in Flexarr": controls what's playing
/// here or on the chosen device (the one Siri plays on, unless another's named). Says so when there's nothing to do
/// (no intro showing, the last episode) or the TV (or server) is too old for it.
struct ControlPlaybackIntent: AppIntent {
    static let title: LocalizedStringResource = "Control playback"
    static let description = IntentDescription("Pauses, resumes or stops what Flexarr is playing, skips the intro, moves on to the next episode, or skips back or forward, here or on another device.",
                                               categoryName: "Playback")

    @Parameter(title: "Action", default: .pause)
    var action: PlaybackAction

    /// How far skipping back or forward goes: 10 seconds, as the remote's buttons, unless a shortcut says otherwise.
    @Parameter(title: "Seconds", default: 10, controlStyle: .field, inclusiveRange: (5, 600))
    var seconds: Int

    @Parameter(title: "On")
    var device: DeviceEntity?

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        When(\.$action, .oneOf, [.back, .forward]) {
            Summary("\(\.$action) \(\.$seconds) seconds on \(\.$device)")
        } otherwise: {
            Summary("\(\.$action) on \(\.$device)")
        }
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        if action == .subtitlesOn || action == .subtitlesOff {
            return .result(dialog: IntentDialog(stringLiteral: try await SubtitlesIntent.change(on: action == .subtitlesOn, device: device, model: model)))
        }
        let target = Play.target(device)
        let jump = Double(action == .back ? -seconds : seconds)
        if target.isHere { return .result(dialog: IntentDialog(stringLiteral: here(jump))) }
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        do {
            switch action {
            case .pause, .resume, .stop:
                let control: DeviceControl = action == .pause ? .pause : action == .resume ? .resume : .stop
                let result = try await client.control(device: target.id, control)
                return .result(dialog: result.delivered ? "Done." : "Flexarr isn't open on \(result.deviceName).")
            case .subtitlesOn, .subtitlesOff:
                return .result(dialog: "Done.") // handled above
            case .skipIntro, .next, .back, .forward:
                // Open, playing, then able to: a TV whose app is closed after a server restart isn't told to update.
                guard let tv = try await client.devices().first(where: { $0.id == target.id }) else { throw FlexarrIntentError.deviceGone(target.name) }
                guard tv.online else { return .result(dialog: "Flexarr isn't open on \(target.name).") }
                guard let playing = tv.nowPlaying else { return .result(dialog: "Nothing is playing on \(target.name).") }
                guard tv.takes(action.kind) else { return .result(dialog: "\(target.name) needs a newer Flexarr.") }
                guard !playing.item.isPrivate || action == .back || action == .forward else { return .result(dialog: "That's private on this profile.") }
                switch action {
                case .skipIntro:
                    guard playing.prompt == .skipIntro || playing.prompt == .skipRecap else { return .result(dialog: "There's no intro to skip.") }
                    _ = try await client.control(device: target.id, .skipSegment)
                case .next:
                    guard playing.hasNext != false else { return .result(dialog: "There's no next episode.") }
                    _ = try await client.control(device: target.id, .next)
                default:
                    try await client.skip(device: target.id, by: jump)
                }
                return .result(dialog: "Done.")
            }
        } catch FlexarrClientError.http(status: 404, _) {
            throw FlexarrIntentError.deviceGone(target.name)
        } catch FlexarrClientError.http(status: 409, let message) {
            return .result(dialog: "\(message ?? "Flexarr isn't open on \(target.name).")")
        }
    }

    /// Does it here, the title in the player or (skips and next) the music or podcast playing; says what came of it.
    @MainActor
    private func here(_ jump: Double) -> String {
        let player = model.player
        switch action {
        case .pause: player.isShowingTitle ? player.pause() : model.music.pause()
        case .resume: player.isShowingTitle ? player.resume() : model.music.resume()
        case .stop: player.closePlayer()
        case .skipIntro:
            guard player.isShowingTitle else { return "Nothing is playing here." }
            guard player.canSkipSegment else { return "There's no intro to skip." }
            player.remote(DeviceCommand(id: UUID().uuidString, kind: .skipSegment, itemId: nil, fromBeginning: false, sentBy: nil, sentAt: Date()))
        case .next:
            if player.isShowingTitle {
                guard player.hasNextTitle else { return "There's no next episode." }
                player.remote(DeviceCommand(id: UUID().uuidString, kind: .next, itemId: nil, fromBeginning: false, sentBy: nil, sentAt: Date()))
            } else if model.music.current != nil {
                model.music.next()
            } else {
                return "Nothing is playing here."
            }
        case .subtitlesOn, .subtitlesOff:
            break // handled before
        case .back, .forward:
            if player.isShowingTitle {
                player.remote(DeviceCommand(id: UUID().uuidString, kind: .skip, itemId: nil, fromBeginning: false, sentBy: nil, sentAt: Date(), seconds: jump))
            } else if model.music.current != nil {
                model.music.skip(by: jump)
            } else {
                return "Nothing is playing here."
            }
        }
        return "Done."
    }
}

/// "Turn on subtitles in Flexarr", here or on the chosen device: the subtitles already chosen, else the first in this
/// device's language that aren't forced-only; or none.
struct SubtitlesIntent: AppIntent {
    static let title: LocalizedStringResource = "Subtitles"
    static let description = IntentDescription("Turns subtitles on (in your language where there are some) or off for what's playing, here or on another device.",
                                               categoryName: "Playback")

    @Parameter(title: "Subtitles", default: .on)
    var state: Switch

    @Parameter(title: "On")
    var device: DeviceEntity?

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Subtitles \(\.$state) on \(\.$device)")
    }

    /// This device's language by name ("English"), as players name their tracks.
    private static var language: String? {
        Locale.current.language.languageCode.flatMap { Locale(identifier: "en").localizedString(forLanguageCode: $0.identifier) }
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        .result(dialog: IntentDialog(stringLiteral: try await Self.change(on: state == .on, device: device, model: model)))
    }

    /// Turns subtitles on or off, here or on `device`, and says what came of it. Siri's "Turn on subtitles in Flexarr"
    /// comes through Control playback (Apple allows ten shortcuts' worth of phrases), which uses this too.
    @MainActor
    static func change(on: Bool, device: DeviceEntity?, model: AppModel) async throws -> String {
        let target = Play.target(device)
        if target.isHere {
            guard model.player.isShowingTitle else { return "Nothing is playing here." }
            let tracks = PlayerTracks(audio: [], subtitles: model.player.subtitleTracks)
            if on, tracks.hasFullSubtitlesOn { return "Subtitles are on." }
            let track = on ? tracks.subtitlesToTurnOn(language: language)?.id : nil
            if on, track == nil { return "This has no subtitles." }
            model.player.remote(DeviceCommand(id: UUID().uuidString, kind: .subtitleTrack, itemId: nil, fromBeginning: false, sentBy: nil, sentAt: Date(), trackId: track))
            return on ? "Subtitles on." : "Subtitles off."
        }
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        do {
            guard let tv = try await client.devices().first(where: { $0.id == target.id }) else { throw FlexarrIntentError.deviceGone(target.name) }
            guard tv.online else { return "Flexarr isn't open on \(target.name)." }
            guard let playing = tv.nowPlaying else { return "Nothing is playing on \(target.name)." }
            guard tv.takes(.subtitleTrack) else { return "\(target.name) needs a newer Flexarr." }
            guard !playing.item.isPrivate else { return "That's private on this profile." }
            var track: String?
            if on {
                let tracks = playing.tracks ?? PlayerTracks(audio: [], subtitles: [])
                if tracks.hasFullSubtitlesOn { return "Subtitles are on." }
                guard let chosen = tracks.subtitlesToTurnOn(language: language) else { return "This has no subtitles." }
                track = chosen.id
            }
            try await client.setSubtitleTrack(device: target.id, track)
            return on ? "Subtitles on." : "Subtitles off."
        } catch FlexarrClientError.http(status: 404, _) {
            throw FlexarrIntentError.deviceGone(target.name)
        } catch FlexarrClientError.http(status: 409, let message) {
            return message ?? "Flexarr isn't open on \(target.name)."
        }
    }
}

// MARK: - Sound

enum Switch: String, AppEnum {
    case on, off

    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "On or off")
    static let caseDisplayRepresentations: [Switch: DisplayRepresentation] = [.on: "On", .off: "Off"]
}

enum BoostLevel: String, AppEnum {
    case off, low, medium, high

    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "Dialogue boost")
    static let caseDisplayRepresentations: [BoostLevel: DisplayRepresentation] = [.off: "Off", .low: "Low", .medium: "Medium", .high: "High"]

    var setting: SoundSettings.DialogueBoost {
        switch self {
        case .off: .off
        case .low: .low
        case .medium: .medium
        case .high: .high
        }
    }
}

/// Changes a sound setting here (`here` changes it and says what happened), or sends it to another device that has
/// Flexarr open: that device's app takes it, from 0.56 on, so what's said is that it was sent.
@MainActor
private func changeSound(on device: DeviceEntity?, model: AppModel, here: (inout SoundSettings) -> String, there: DeviceSound) async throws -> String {
    let target = Play.target(device)
    if target.isHere {
        var settings = SoundSettings.current
        let said = here(&settings)
        SoundSettings.current = settings
        return said
    }
    guard let client = model.client else { throw FlexarrIntentError.notConnected }
    do {
        _ = try await client.setSound(device: target.id, there)
        return "Sent to \(target.name)."
    } catch FlexarrClientError.http(status: 409, let message) {
        // Not open there, or its app is too old: the server says which.
        return message ?? "Flexarr isn't open on \(target.name)."
    } catch FlexarrClientError.http(status: 404, _) {
        throw FlexarrIntentError.deviceGone(target.name)
    }
}

/// "Turn on night mode in Flexarr": quietens loud scenes, here or on another device.
struct NightModeIntent: AppIntent {
    static let title: LocalizedStringResource = "Night mode"
    static let description = IntentDescription("Turns night mode on or off: loud scenes quieter, quiet ones easier to hear.", categoryName: "Sound")

    @Parameter(title: "Night mode", default: .on)
    var state: Switch

    @Parameter(title: "On")
    var device: DeviceEntity?

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Turn \(\.$state) night mode on \(\.$device)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        let on = state == .on
        let said = try await changeSound(on: device, model: model, here: { settings in
            // On a schedule that's already doing what was asked, the schedule stays.
            if settings.nightMode == .scheduled {
                let now = settings.isNight(at: Date())
                if now == on {
                    return on ? "Night mode's already on, on its schedule." : "Night mode's off, and comes on at \(SoundSettings.clock(settings.nightStart)) as scheduled."
                }
                settings.nightMode = on ? .on : .off
                return on ? "Night mode's on. That replaces its schedule." : "Night mode's off. That replaces its schedule."
            }
            settings.nightMode = on ? .on : .off
            return on ? "Night mode's on." : "Night mode's off."
        }, there: DeviceSound(nightMode: on ? "On" : "Off"))
        return .result(dialog: "\(said)")
    }
}

/// Dialogue boost from Shortcuts: makes voices clearer, here or on another device.
struct DialogueBoostIntent: AppIntent {
    static let title: LocalizedStringResource = "Dialogue boost"
    static let description = IntentDescription("Sets how much voices are lifted over music and effects.", categoryName: "Sound")

    @Parameter(title: "Dialogue boost", default: .medium)
    var level: BoostLevel

    @Parameter(title: "On")
    var device: DeviceEntity?

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Set dialogue boost to \(\.$level) on \(\.$device)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        let boost = level.setting
        let said = try await changeSound(on: device, model: model, here: { settings in
            settings.dialogueBoost = boost
            return boost == .off ? "Dialogue boost's off." : "Dialogue boost's \(boost.rawValue.lowercased())."
        }, there: DeviceSound(dialogueBoost: boost.rawValue))
        return .result(dialog: "\(said)")
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
                // No title in the phrase: Siri asks which show (where a phrase can't carry one).
                "Shuffle a show in \(.applicationName)",
            ],
            shortTitle: "Shuffle a show",
            systemImageName: "shuffle"
        )
        AppShortcut(
            intent: AddToWatchlistIntent(),
            phrases: [
                "Add \(\.$title) to my watchlist in \(.applicationName)",
                "Add \(\.$title) to my \(.applicationName) watchlist",
                "Put \(\.$title) on my watchlist in \(.applicationName)",
            ],
            shortTitle: "Add to watchlist",
            systemImageName: "bookmark"
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
            intent: ControlPlaybackIntent(),
            phrases: [
                "\(\.$action) \(.applicationName)",
                "\(\.$action) in \(.applicationName)",
                "\(\.$action) playback in \(.applicationName)",
            ],
            shortTitle: "Control playback",
            systemImageName: "playpause.fill"
        )
        AppShortcut(
            intent: NightModeIntent(),
            phrases: [
                "Turn \(\.$state) night mode in \(.applicationName)",
                "Night mode \(\.$state) in \(.applicationName)",
            ],
            shortTitle: "Night mode",
            systemImageName: "moon"
        )
    }
}
