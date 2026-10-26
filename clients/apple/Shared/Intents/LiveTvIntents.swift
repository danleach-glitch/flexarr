import AppIntents
import FlexarrKit
import Foundation

// Live TV for Siri and Shortcuts: "Put on BBC One in Flexarr" (here, or on the TV chosen in Settings), "What's on BBC
// One in Flexarr" (said back, nothing opened), and recording what's on next (an action in Shortcuts).

/// A Live TV channel, as Siri and Shortcuts see it.
struct ChannelEntity: AppEntity {
    static let typeDisplayRepresentation = TypeDisplayRepresentation(name: "TV channel")
    static let defaultQuery = ChannelQuery()

    let id: String
    let name: String
    let number: String

    init(_ channel: LiveChannel) {
        id = channel.id
        name = channel.name
        number = channel.numberText
    }

    var displayRepresentation: DisplayRepresentation {
        DisplayRepresentation(title: "\(name)", subtitle: "\(number)")
    }
}

/// Finds channels by what was said ("BBC 1", "BBC one", "channel four", "one oh one"), and tells Siri their names.
struct ChannelQuery: EntityStringQuery {
    @Dependency private var model: AppModel

    @MainActor
    func entities(for identifiers: [String]) async throws -> [ChannelEntity] {
        let all = try await channels()
        return identifiers.compactMap { id in all.first { $0.id == id }.map(ChannelEntity.init) }
    }

    @MainActor
    func entities(matching string: String) async throws -> [ChannelEntity] {
        let all = try await channels()
        let said = Self.spoken(string)
        let exact = all.filter { Self.spoken($0.name) == said || $0.numberText == said }
        if !exact.isEmpty { return exact.map(ChannelEntity.init) }
        return all.filter { Self.spoken($0.name).contains(said) }.map(ChannelEntity.init)
    }

    /// Favourites first: Siri listens for these names.
    @MainActor
    func suggestedEntities() async throws -> [ChannelEntity] {
        let all = try await channels()
        return (all.filter(\.isFavourite) + all.filter { !$0.isFavourite }).map(ChannelEntity.init)
    }

    @MainActor
    private func channels() async throws -> [LiveChannel] {
        guard let client = model.client else { return [] }
        return (try? await client.liveChannels()) ?? []
    }

    private static let numbers = ["zero": "0", "one": "1", "two": "2", "three": "3", "four": "4", "five": "5", "six": "6", "seven": "7",
                                  "eight": "8", "nine": "9", "ten": "10"]

    /// A name as it sounds: lowercased, number words as digits, "HD" and punctuation dropped, no spaces ("BBC One HD"
    /// and "bbc 1" both become "bbc1").
    static func spoken(_ text: String) -> String {
        text.lowercased()
            .split(whereSeparator: { !$0.isLetter && !$0.isNumber })
            .map { numbers[String($0)] ?? String($0) }
            .filter { $0 != "hd" && $0 != "uk" && $0 != "the" }
            .joined()
    }
}

/// "Put on BBC One in Flexarr": the channel, here or on the TV chosen in Settings (or the device named).
struct WatchChannelIntent: AppIntent {
    static let title: LocalizedStringResource = "Put on a TV channel"
    static let description = IntentDescription("Puts a Live TV channel on: here, or on another device signed in to your Flexarr server.",
                                               categoryName: "Live TV")

    @Parameter(title: "Channel", requestValueDialog: "Which channel?")
    var channel: ChannelEntity

    @Parameter(title: "On")
    var device: DeviceEntity?

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Put on \(\.$channel) on \(\.$device)")
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog & OpensIntent {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        let target = Play.target(device)
        if target.isHere {
            guard let url = URL(string: "flexarr://live/\(channel.id)") else { throw FlexarrIntentError.nothingToPlay(channel.name) }
            let opened: Play.Result = .result(opensIntent: OpenURLIntent(url), dialog: "Putting on \(channel.name).")
            return opened
        }
        let result: DeviceCommandResult
        do {
            result = try await client.watch(on: target.id, channelId: channel.id)
        } catch FlexarrClientError.http(status: 404, _) {
            throw FlexarrIntentError.deviceGone(target.name)
        }
        return Play.Result.result(dialog: result.delivered
            ? "Putting \(channel.name) on \(result.deviceName)."
            : "Flexarr isn't open on \(result.deviceName). \(channel.name) will go on when you open it in the next two minutes.")
    }
}

/// "What's on BBC One in Flexarr": what's on now and next, said back.
struct WhatsOnIntent: AppIntent {
    static let title: LocalizedStringResource = "What's on a TV channel"
    static let description = IntentDescription("Says what's on a Live TV channel now, and what's next.", categoryName: "Live TV")

    @Parameter(title: "Channel", requestValueDialog: "Which channel?")
    var channel: ChannelEntity

    @Dependency private var model: AppModel

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        guard let live = try await client.liveChannels().first(where: { $0.id == channel.id }) else { throw FlexarrIntentError.nothingToPlay(channel.name) }
        var sentences: [String] = []
        if let now = live.now {
            sentences.append("On \(live.name) now: \(now.displayTitle), until \(Self.clock.string(from: now.end)).")
        } else {
            sentences.append("\(live.name) has no listings.")
        }
        if let next = live.next {
            sentences.append("Then \(next.displayTitle) at \(Self.clock.string(from: next.start)).")
        }
        return .result(dialog: "\(sentences.joined(separator: " "))")
    }

    private static let clock: DateFormatter = {
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        return formatter
    }()
}

/// Recording what's next on a channel: an action in Shortcuts (Siri's phrases are all taken), to give a phrase of your own.
struct RecordNextIntent: AppIntent {
    static let title: LocalizedStringResource = "Record what's next on a TV channel"
    static let description = IntentDescription("Sets the programme after the one on now to record, on a Live TV channel.", categoryName: "Live TV")

    @Parameter(title: "Channel", requestValueDialog: "Which channel?")
    var channel: ChannelEntity

    @Parameter(title: "What's on now instead", default: false)
    var now: Bool

    @Dependency private var model: AppModel

    static var parameterSummary: some ParameterSummary {
        Summary("Record what's next on \(\.$channel)") {
            \.$now
        }
    }

    @MainActor
    func perform() async throws -> some IntentResult & ProvidesDialog {
        guard let client = model.client else { throw FlexarrIntentError.notConnected }
        guard let live = try await client.liveChannels().first(where: { $0.id == channel.id }),
              let programme = now ? live.now : live.next
        else { throw FlexarrIntentError.nothingToPlay(channel.name) }
        let recording = try await client.record(programme: programme.id)
        return .result(dialog: recording.clashes
            ? "\(programme.title) is on the list, but it clashes with another recording, so it won't record unless one comes off."
            : "Recording \(programme.title) on \(live.name).")
    }
}
