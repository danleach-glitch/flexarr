import Foundation
import os

/// How fast streams reach this device from the server lately, for the start request's `measuredBitrate`
/// (docs/fast-start.md). Samples are stream rates the player measured (AVPlayer's observed bitrate, each an average over a
/// stream's own downloads), never artwork: small files loaded side by side read far below the link. The upper quartile of
/// the last few, for half an hour; nothing at all with no sample (better to stream as at home than to cap on a guess).
public final class LinkSpeed: Sendable {
    public static let shared = LinkSpeed()

    /// Downloads smaller than this are mostly the round trip, not the speed.
    public static let minSampleBytes = 200_000
    static let kept = 12
    static let freshFor: TimeInterval = 30 * 60

    private struct Sample { let bitsPerSecond: Int; let at: Date }
    private let samples = OSAllocatedUnfairLock<[Sample]>(initialState: [])

    public init() {}

    /// Notes `bytes` arriving over `seconds` (from the first byte to the last).
    public func sample(bytes: Int64, seconds: TimeInterval, at now: Date = Date()) {
        guard bytes >= Self.minSampleBytes, seconds > 0 else { return }
        sample(bitsPerSecond: Int(Double(bytes) * 8 / seconds), at: now)
    }

    /// Notes a rate the player measured itself.
    public func sample(bitsPerSecond: Int, at now: Date = Date()) {
        guard bitsPerSecond > 0 else { return }
        samples.withLock { list in
            list = Array((list.filter { now.timeIntervalSince($0.at) < Self.freshFor } + [Sample(bitsPerSecond: bitsPerSecond, at: now)]).suffix(Self.kept))
        }
    }

    /// The measured speed in bits per second, or nil with nothing recent to go on.
    public func current(at now: Date = Date()) -> Int? {
        let fresh = samples.withLock { $0 }.filter { now.timeIntervalSince($0.at) < Self.freshFor }.map(\.bitsPerSecond).sorted()
        return fresh.isEmpty ? nil : fresh[Int(Double(fresh.count) * 0.75)]
    }
}
