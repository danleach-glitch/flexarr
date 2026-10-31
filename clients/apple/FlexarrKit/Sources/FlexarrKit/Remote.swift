import Foundation

// What the iPhone's remote works out from the device list: which TVs are playing, and when the times its Live Activity
// shows have changed enough to update it.

extension Array where Element == Device {
    /// The TVs playing something (not this device, not phones or tablets), the one used last first. A TV playing but
    /// silent for `silence` seconds has gone (its app killed, the TV asleep); one paused that has closed Flexarr for a
    /// minute too. Paused and open, it stays until the server lets it go. Given `profileId`, only that profile's.
    public func playingTVs(at now: Date = Date(), silence: TimeInterval = 45, for profileId: String? = nil) -> [Device] {
        filter { device in
            guard !device.isThisOne, device.isTV, let playing = device.nowPlaying else { return false }
            // Only what the phone's own profile is watching: Claire's film isn't on Dan's phone.
            if let profileId, (playing.profileId ?? device.profileId) != profileId { return false }
            guard let reportedAt = playing.reportedAt else { return true }
            let quiet = now.timeIntervalSince(reportedAt)
            return playing.paused ? device.online || quiet < 60 : quiet < silence
        }
        .sorted { $0.lastUsedAt > $1.lastUsedAt }
    }
}

extension Array where Element == Device {
    /// The same devices, playing the same, as `other`: report times a moment apart (each list works its own out from
    /// the server's report age) count as the same.
    public func matches(_ other: [Device]) -> Bool {
        guard count == other.count else { return false }
        return zip(self, other).allSatisfy { mine, theirs in
            guard let a = mine.nowPlaying, let b = theirs.nowPlaying, let at = a.reportedAt, let bt = b.reportedAt else { return mine == theirs }
            guard abs(at.timeIntervalSince(bt)) < 1 else { return false }
            var same = theirs
            same.nowPlaying = NowPlaying(item: b.item, positionSeconds: b.positionSeconds, durationSeconds: b.durationSeconds, paused: b.paused, reportedAt: at,
                                         rate: b.rate, prompt: b.prompt, hasNext: b.hasNext, tracks: b.tracks, sound: b.sound)
            return mine == same
        }
    }
}

extension ItemSummary {
    /// A title from a library hidden from this profile, playing somewhere: shown as playing, with nothing to say what
    /// it is (or to act on it with).
    public var isPrivate: Bool { id == "00000000-0000-0000-0000-000000000000" }
}

extension PlayerTracks {
    /// The subtitles to turn on: those already on; otherwise the first in `language` (a name such as "English") that
    /// isn't forced-only, then the first that isn't forced-only, then the first.
    public func subtitlesToTurnOn(language: String?) -> PlayerTrack? {
        if let on = subtitles.first(where: { $0.selected && !$0.isForced }) { return on }
        let full = subtitles.filter { !$0.isForced }
        if let language, let match = full.first(where: { $0.label.localizedCaseInsensitiveContains(language) }) { return match }
        return full.first ?? subtitles.first
    }
}

extension PlayerTracks {
    /// Subtitles are on, and not only forced ones (signs and foreign dialogue).
    public var hasFullSubtitlesOn: Bool { subtitles.contains { $0.selected && !$0.isForced } }
}

extension PlayerTrack {
    /// Forced subtitles only: signs and foreign dialogue, not the whole film.
    public var isForced: Bool { label.localizedCaseInsensitiveContains("forced") }
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
