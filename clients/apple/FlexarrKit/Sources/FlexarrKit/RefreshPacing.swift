import Foundation

/// Lets something that's asked for often happen at most once in a while. A Mac's window becomes active on every
/// Cmd-Tab or click back into it, not a few times a day as a phone's app comes to the front, so what an app does
/// "on coming to the front" (reload, ask the server how it is) is paced there.
///
/// Kept per `key` (the server and profile, say): a different one is due at once.
public final class RefreshPacing: @unchecked Sendable {
    private let interval: TimeInterval
    private let lock = NSLock()
    private var last: (key: String, at: Date)?

    public init(every interval: TimeInterval) {
        self.interval = interval
    }

    /// Whether it's time again for `key`; saying yes starts the wait over.
    public func isDue(for key: String = "", now: Date = Date()) -> Bool {
        lock.withLock {
            if let last, last.key == key {
                let since = now.timeIntervalSince(last.at)
                // A clock put back counts as time having passed, rather than never being due again.
                if since >= 0, since < interval { return false }
            }
            last = (key, now)
            return true
        }
    }
}
