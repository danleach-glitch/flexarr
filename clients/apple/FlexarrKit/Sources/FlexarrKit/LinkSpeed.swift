import Foundation
import os

/// How fast data reaches this device from the server lately, for the start request's `measuredBitrate`
/// (docs/fast-start.md): each big enough download (artwork, a stream) is a sample of its body's bytes over the time
/// they took to arrive, not counting the wait for the first byte. The median of the last few, for half an hour.
public final class LinkSpeed: Sendable {
    public static let shared = LinkSpeed()

    /// Downloads smaller than this are mostly the round trip, not the speed.
    public static let minSampleBytes = 200_000
    static let kept = 8
    static let freshFor: TimeInterval = 30 * 60

    private struct Sample { let bitsPerSecond: Int; let at: Date }
    private let samples = OSAllocatedUnfairLock<[Sample]>(initialState: [])

    public init() {}

    /// Notes `bytes` arriving over `seconds` (from the first byte to the last).
    public func sample(bytes: Int64, seconds: TimeInterval, at now: Date = Date()) {
        guard bytes >= Self.minSampleBytes, seconds > 0 else { return }
        let bps = Int(Double(bytes) * 8 / seconds)
        samples.withLock { list in
            list = Array((list.filter { now.timeIntervalSince($0.at) < Self.freshFor } + [Sample(bitsPerSecond: bps, at: now)]).suffix(Self.kept))
        }
    }

    /// A finished download's own timings, as URLSession measured them.
    public func sample(_ metrics: URLSessionTaskMetrics) {
        guard let transaction = metrics.transactionMetrics.last, transaction.resourceFetchType == .networkLoad,
              let first = transaction.responseStartDate, let last = transaction.responseEndDate else { return }
        sample(bytes: transaction.countOfResponseBodyBytesReceived, seconds: last.timeIntervalSince(first), at: last)
    }

    /// The measured speed in bits per second, or nil with nothing recent to go on.
    public func current(at now: Date = Date()) -> Int? {
        let fresh = samples.withLock { $0 }.filter { now.timeIntervalSince($0.at) < Self.freshFor }.map(\.bitsPerSecond).sorted()
        return fresh.isEmpty ? nil : fresh[fresh.count / 2]
    }
}

/// Collects one task's metrics for `LinkSpeed` (pass as a download's delegate).
public final class LinkSpeedSampler: NSObject, URLSessionTaskDelegate, Sendable {
    public static let shared = LinkSpeedSampler()

    public func urlSession(_ session: URLSession, task: URLSessionTask, didFinishCollecting metrics: URLSessionTaskMetrics) {
        LinkSpeed.shared.sample(metrics)
    }
}
