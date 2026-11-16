import Foundation
import os

/// The speed test away from home (docs/remote-streaming.md): a few megabytes from the server, timed, so the first play
/// starts on a quality the connection carries rather than on a guess. Run when the app opens or comes to the front
/// away from home, and from a title's page with no fresh reading; at most every 10 minutes per link, unless the
/// network has changed since. 4 MB on Wi-Fi, 1 MB on mobile data (or a network that says it's expensive or limited).
public final class SpeedTest: Sendable {
    public static let shared = SpeedTest()

    public static let wifiBytes = 4_000_000
    public static let mobileBytes = 1_000_000
    /// The least time between two tests on the same link and network.
    public static let interval: TimeInterval = 10 * 60

    private struct State {
        var last: (link: String, at: Date)?
        var networkChanged = false
        var running = false
    }
    private let state = OSAllocatedUnfairLock(initialState: State())

    public init() {}

    /// How much to fetch: less on mobile data.
    public static func bytes(expensive: Bool) -> Int { expensive ? mobileBytes : wifiBytes }

    /// `bytes` over `seconds`, as bits per second; nil when it's too small or quick to say anything.
    public static func bitsPerSecond(bytes: Int64, seconds: TimeInterval) -> Int? {
        guard bytes >= Int64(LinkSpeed.minSampleBytes), seconds > 0.001 else { return nil }
        return Int(Double(bytes) * 8 / seconds)
    }

    /// The device moved to another network (or lost it and came back): the next chance tests again.
    public func networkChanged() {
        state.withLock { $0.networkChanged = true }
    }

    /// Whether a test on `link` is due at `now`: none running, and none on this link in the last 10 minutes unless the
    /// network has changed since.
    public func isDue(link: String, now: Date = Date()) -> Bool {
        state.withLock { state in
            guard !state.running else { return false }
            guard let last = state.last, last.link == link, !state.networkChanged else { return true }
            return now.timeIntervalSince(last.at) >= Self.interval
        }
    }

    /// Runs a test if one is due and the app is away from home, and notes its reading in `speed`. `onlyIfStale`: only
    /// with no fresh reading (a title's page). Returns the reading, or nil when none was taken.
    @discardableResult
    public func runIfDue(_ client: FlexarrClient, expensive: Bool, onlyIfStale: Bool = false, speed: LinkSpeed = .shared,
                         now: Date = Date()) async -> Int? {
        guard client.addresses.awayFromHome else { return nil }
        if onlyIfStale, speed.fresh(at: now) != nil { return nil }
        let link = client.addresses.linkKey
        let starting = state.withLock { state -> Bool in
            guard !state.running else { return false }
            if let last = state.last, last.link == link, !state.networkChanged, now.timeIntervalSince(last.at) < Self.interval { return false }
            state.running = true
            state.networkChanged = false
            return true
        }
        guard starting else { return nil }
        let reading = try? await client.speedTest(bytes: Self.bytes(expensive: expensive))
        state.withLock {
            $0.running = false
            // A server without the test (nil) is let be for the interval too.
            $0.last = (link, now)
        }
        guard let reading else { return nil }
        // Kept for the link it was measured over, even if home has answered again meanwhile.
        speed.sample(bitsPerSecond: reading, link: link, at: now)
        return reading
    }
}
