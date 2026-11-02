import Foundation

/// How a sitting in the player ended, so the page Play was pressed on can move on: that page (`from`, a token it
/// made), the title it was started with, the one it stopped on, whether that one was watched, and the episode after.
public struct PlaybackEnding: Equatable, Sendable {
    public let from: String?
    public let startedWith: String
    public let stoppedOn: String
    public let watched: Bool
    public let next: String?

    public init(from: String?, startedWith: String, stoppedOn: String, watched: Bool, next: String?) {
        self.from = from
        self.startedWith = startedWith
        self.stoppedOn = stoppedOn
        self.watched = watched
        self.next = next
    }

    /// Where a page showing `shown` goes now: past an episode watched to its end, or on to the one the player had
    /// moved on to and stopped part way through. Nil to stay (a show's page, the last episode, stopped part way).
    public func moveOn(from shown: String) -> String? {
        guard shown == startedWith else { return nil }
        let target = watched ? next : stoppedOn
        return target == startedWith ? nil : target
    }

    /// Watched as the server counts it: into the credits or past 92%, whichever comes first.
    public static func isWatched(position: Double, duration: Double, creditsStart: Double?) -> Bool {
        guard position > 0 else { return false }
        return position >= min(creditsStart ?? .infinity, duration > 0 ? duration * 0.92 : .infinity)
    }
}
