import ActivityKit
import AppIntents
import Foundation

/// The Live Activity for what a TV in the house is playing: on the Lock Screen and in the Dynamic Island while the
/// iPhone's remote follows it. The title and the TV's name are set by the app when it starts it; the server keeps
/// only the times up to date (through the relay, which never sees what's playing).
struct TVPlayingAttributes: ActivityAttributes {
    /// The times, as the server sends them (`LiveActivityState`): the same keys and kinds, or updates don't decode.
    struct ContentState: Codable, Hashable {
        var paused: Bool
        var position: Double
        /// When `position` was so, in seconds since 1970.
        var at: Double
        var duration: Double
        var rate: Double

        /// Where it has got to by `date`.
        func position(at date: Date) -> Double {
            guard !paused else { return position }
            let moved = position + max(0, date.timeIntervalSince1970 - at) * rate
            return duration > 0 ? min(moved, duration) : moved
        }

        /// When it started, as the clock runs, and when it will end: for a progress bar that moves by itself.
        var playingRange: ClosedRange<Date>? {
            guard !paused, rate > 0, duration > position else { return nil }
            let start = Date(timeIntervalSince1970: at - position / rate)
            return start...start.addingTimeInterval(duration / rate)
        }
    }

    let deviceId: String
    let deviceName: String
    let itemId: String
    let title: String
    let subtitle: String?
    /// The title's colour, "#rrggbb".
    let accent: String?
    /// A small 16:9 picture in the app group's folder (`PodcastSnapshot.artwork`), when one was saved.
    let artworkFile: String?
}

/// Play/pause and the jumps on the Live Activity's buttons. A Live Activity intent, so the system runs it in the app
/// (which has the sign-in), waking it if need be; the widget only shows the buttons.
struct TVRemoteIntent: LiveActivityIntent {
    static let title: LocalizedStringResource = "Control the TV"
    static let isDiscoverable = false

    @Parameter(title: "TV")
    var deviceId: String

    /// "pause", "resume", "back" or "forward".
    @Parameter(title: "Action")
    var action: String

    init() {}

    init(deviceId: String, action: String) {
        self.deviceId = deviceId
        self.action = action
    }

    #if WIDGET
    func perform() async throws -> some IntentResult { .result() }
    #else
    @Dependency private var model: AppModel

    @MainActor
    func perform() async throws -> some IntentResult {
        guard let client = model.client else { return .result() }
        switch action {
        case "pause": _ = try? await client.control(device: deviceId, .pause)
        case "resume": _ = try? await client.control(device: deviceId, .resume)
        case "back": try? await client.skip(device: deviceId, by: -10)
        case "forward": try? await client.skip(device: deviceId, by: 10)
        default: break
        }
        return .result()
    }
    #endif
}
