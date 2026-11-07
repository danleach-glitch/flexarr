import Foundation

/// When the lock screen's playing time needs telling again. The system counts the time on by itself from the last
/// position, rate and when they were given, so while playback runs as told there's nothing to send: only when where
/// it really is has drifted from that count (a stall, a jump the player made on its own), or the rate or length has
/// changed, is it told again.
public struct NowPlayingClock: Sendable {
    /// How far the system's count may be from where playback is before it's told again, in seconds.
    public static let tolerance: Double = 1.5

    private var told: (position: Double, duration: Double, rate: Double, at: Date)?

    public init() {}

    /// The time was just given to the system.
    public mutating func told(position: Double, duration: Double, rate: Double, at now: Date = Date()) {
        told = (position, duration, rate, now)
    }

    /// The system's count is off: it needs telling where playback is.
    public func isOff(position: Double, duration: Double, rate: Double, at now: Date = Date()) -> Bool {
        guard let told else { return true }
        guard rate == told.rate, duration.isFinite == told.duration.isFinite,
              !duration.isFinite || abs(duration - told.duration) <= 0.5 else { return true }
        let since = now.timeIntervalSince(told.at)
        // A clock put back counts as being off.
        guard since >= 0 else { return true }
        let counted = told.position + told.rate * since
        return !position.isFinite || abs(counted - position) > Self.tolerance
    }
}
