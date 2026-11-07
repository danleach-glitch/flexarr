import Foundation

/// How often a transfer's progress is shown. The system reports it many times a second, and each step shown redraws
/// every screen that lists downloads, so a step is shown only when its whole percent has moved on, and then at most
/// every `interval` for the same transfer.
public enum ProgressPacing {
    /// The shortest time between two steps shown of one transfer: a few a second.
    public static let interval: TimeInterval = 0.25

    /// Whether `fraction` is worth showing over `shown` (nil when the transfer isn't showing progress yet: always
    /// shown), last shown at `shownAt`.
    public static func shows(_ fraction: Double, after shown: Double?, shownAt: Date?, now: Date = Date()) -> Bool {
        guard let shown, fraction.isFinite, shown.isFinite else { return true }
        guard (fraction * 100).rounded(.down) != (shown * 100).rounded(.down) else { return false }
        guard let shownAt else { return true }
        let since = now.timeIntervalSince(shownAt)
        // A clock put back counts as time having passed.
        return since < 0 || since >= interval
    }
}
