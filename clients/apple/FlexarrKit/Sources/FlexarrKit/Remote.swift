import Foundation

// What the iPhone's remote works out from the device list: which TVs are playing, and when the times its Live Activity
// shows have changed enough to update it.

extension Array where Element == Device {
    /// The TVs playing something (not this device, not phones or tablets), the one used last first. A TV playing but
    /// silent for `silence` seconds has gone (its app killed, the TV asleep); one paused that has closed Flexarr for a
    /// minute too. Paused and open, it stays until the server lets it go.
    public func playingTVs(at now: Date = Date(), silence: TimeInterval = 45) -> [Device] {
        filter { device in
            guard !device.isThisOne, device.isTV, let playing = device.nowPlaying else { return false }
            guard let reportedAt = playing.reportedAt else { return true }
            let quiet = now.timeIntervalSince(reportedAt)
            return playing.paused ? device.online || quiet < 60 : quiet < silence
        }
        .sorted { $0.lastUsedAt > $1.lastUsedAt }
    }
}

/// The times a Live Activity shows for a TV, as the server sends them (`LiveActivityState`).
public struct RemoteTimes: Sendable, Hashable {
    public var paused: Bool
    public var position: Double
    /// When `position` was so, in seconds since 1970.
    public var at: Double
    public var duration: Double
    public var rate: Double

    public init(paused: Bool, position: Double, at: Double, duration: Double, rate: Double) {
        self.paused = paused
        self.position = position
        self.at = at
        self.duration = duration
        self.rate = rate
    }

    public init(_ playing: NowPlaying, now: Date = Date()) {
        self.init(paused: playing.paused, position: playing.positionSeconds, at: (playing.reportedAt ?? now).timeIntervalSince1970,
                  duration: playing.durationSeconds, rate: playing.rate ?? 1)
    }

    /// Where it has got to by `date` (seconds since 1970).
    public func position(at date: Double) -> Double {
        guard !paused else { return position }
        let moved = position + max(0, date - at) * rate
        return duration > 0 ? min(moved, duration) : moved
    }

    /// Shows something the activity's clock, running from `shown`, wouldn't: a pause, a new speed, a seek (3 s or more).
    public func isNews(after shown: RemoteTimes) -> Bool {
        if paused != shown.paused || abs(rate - shown.rate) > 0.01 || abs(duration - shown.duration) > 1 { return true }
        return abs(position - shown.position(at: at)) > 3
    }
}
