import Foundation
import os

/// How fast streams reach this device from the server lately, for the start request's `measuredBitrate`
/// (docs/fast-start.md, docs/remote-streaming.md). Samples are stream rates the player measured (AVPlayer's observed
/// bitrate, each an average over a stream's own downloads), the speed test's, and the engine's buffer growth; never
/// artwork: small files loaded side by side read far below the link. The median of the last few (the lower middle one
/// when there's an even number): a link that swings is judged by a typical reading, not its good moments (#539).
///
/// Readings are kept per server and per address in use (home, the away address, a guest's tunnel: `ServerAddresses.
/// linkKey`), across launches: fresh for half an hour, then a starting point for a day, so the first play away from
/// home isn't a blind one. Nothing at all with no reading (better to stream as at home than to cap on a guess).
public final class LinkSpeed: @unchecked Sendable {
    public static let shared = LinkSpeed(memory: .standard)

    /// Downloads smaller than this are mostly the round trip, not the speed.
    public static let minSampleBytes = 200_000
    static let kept = 12
    /// How long a reading says how the link is now.
    public static let freshFor: TimeInterval = 30 * 60
    /// How long a reading is still a better start than nothing.
    public static let startingPointFor: TimeInterval = 24 * 60 * 60
    static let memoryKey = "io.github.flexarr.linkSpeed"

    private struct Sample: Codable { let bitsPerSecond: Int; let at: Date }
    private struct State {
        /// Where readings go and come from now: the followed addresses' link, else `link`.
        var addresses: ServerAddresses?
        var link = ""
        var samples: [String: [Sample]] = [:]
    }
    private let state = OSAllocatedUnfairLock(initialState: State())
    /// Where readings are kept between launches; nil keeps nothing.
    private let memory: UserDefaults?

    public init(memory: UserDefaults? = nil) {
        self.memory = memory
        if let data = memory?.data(forKey: Self.memoryKey),
           let saved = try? JSONDecoder().decode([String: [Sample]].self, from: data) {
            state.withLock { $0.samples = saved }
        }
    }

    /// Readings from now on belong to these addresses' link (the server and the address in use), whichever address
    /// that comes to be. Nil: the plain `link` set.
    public func follow(_ addresses: ServerAddresses?) {
        state.withLock { $0.addresses = addresses }
    }

    /// The link readings belong to when no addresses are followed (tests, a single address).
    public func use(link: String) {
        state.withLock {
            $0.addresses = nil
            $0.link = link
        }
    }

    /// The link readings go to now.
    public var link: String {
        state.withLock { $0.addresses?.linkKey ?? $0.link }
    }

    /// Notes `bytes` arriving over `seconds` (from the first byte to the last).
    public func sample(bytes: Int64, seconds: TimeInterval, at now: Date = Date()) {
        guard bytes >= Self.minSampleBytes, seconds > 0 else { return }
        sample(bitsPerSecond: Int(Double(bytes) * 8 / seconds), at: now)
    }

    /// Notes a rate the player measured itself. `link`: the one it was measured over, if not the one in use now.
    public func sample(bitsPerSecond: Int, link given: String? = nil, at now: Date = Date()) {
        guard bitsPerSecond > 0 else { return }
        let saved = state.withLock { state -> [String: [Sample]] in
            let link = given ?? state.addresses?.linkKey ?? state.link
            let kept = (state.samples[link] ?? []).filter { now.timeIntervalSince($0.at) < Self.startingPointFor }
            state.samples[link] = Array((kept + [Sample(bitsPerSecond: bitsPerSecond, at: now)]).suffix(Self.kept))
            // Other links' day-old readings go as this is saved.
            state.samples = state.samples.compactMapValues { list in
                let left = list.filter { now.timeIntervalSince($0.at) < Self.startingPointFor }
                return left.isEmpty ? nil : left
            }
            return state.samples
        }
        if let memory, let data = try? JSONEncoder().encode(saved) { memory.set(data, forKey: Self.memoryKey) }
    }

    /// The measured speed in bits per second: the fresh readings' median, else the last day's as a starting point, or
    /// nil with nothing to go on.
    public func current(at now: Date = Date()) -> Int? {
        fresh(at: now) ?? median(within: Self.startingPointFor, at: now)
    }

    /// The median of the last half hour's readings, or nil with none (time for a speed test).
    public func fresh(at now: Date = Date()) -> Int? {
        median(within: Self.freshFor, at: now)
    }

    private func median(within age: TimeInterval, at now: Date) -> Int? {
        let list = state.withLock { $0.samples[$0.addresses?.linkKey ?? $0.link] ?? [] }
        let rates = list.filter { now.timeIntervalSince($0.at) < age }.map(\.bitsPerSecond).sorted()
        return rates.isEmpty ? nil : rates[(rates.count - 1) / 2]
    }
}
