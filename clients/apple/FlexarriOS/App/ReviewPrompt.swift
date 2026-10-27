import Foundation
import Observation

/// When to show Apple's "Enjoying Flexarr?" rating prompt: after a film or episode is watched to the end and the
/// player has closed, once Flexarr has been watched this way a few times on more than one day, at least a week after
/// it was first opened, and at most once a version. Apple decides whether the prompt actually appears.
@Observable
final class ReviewPrompt {
    /// The App Store's page for writing a review, for Rate Flexarr in Settings.
    static let writeReviewURL = URL(string: "https://apps.apple.com/app/id6826618084?action=write-review")!

    private static let watchesNeeded = 3
    private static let daysNeeded = 2
    private static let waitAfterFirstOpen: TimeInterval = 7 * 24 * 60 * 60

    /// Set when the prompt should come up now; `asked()` clears it.
    private(set) var isDue = false

    @ObservationIgnored private let defaults: UserDefaults
    @ObservationIgnored private let version: String

    init(defaults: UserDefaults = .standard, version: String = Bundle.main.shortVersion) {
        self.defaults = defaults
        self.version = version
        if defaults.object(forKey: Key.firstOpened) == nil {
            defaults.set(Date(), forKey: Key.firstOpened)
        }
    }

    /// Something was watched into its credits. `closing`: the player is going away, so now is a good time to ask.
    func watchedToEnd(closing: Bool, now: Date = Date()) {
        let day = Self.day(now)
        var days = defaults.stringArray(forKey: Key.days) ?? []
        if !days.contains(day) { days = Array((days + [day]).suffix(Self.daysNeeded)) }
        defaults.set(days, forKey: Key.days)
        defaults.set(defaults.integer(forKey: Key.watches) + 1, forKey: Key.watches)
        if closing, shouldAsk(now: now) { isDue = true }
    }

    /// The prompt was asked for: not again this version.
    func asked() {
        isDue = false
        defaults.set(version, forKey: Key.askedVersion)
        defaults.set(0, forKey: Key.watches)
        defaults.removeObject(forKey: Key.days)
    }

    private func shouldAsk(now: Date) -> Bool {
        guard defaults.string(forKey: Key.askedVersion) != version,
              defaults.integer(forKey: Key.watches) >= Self.watchesNeeded,
              (defaults.stringArray(forKey: Key.days) ?? []).count >= Self.daysNeeded,
              let firstOpened = defaults.object(forKey: Key.firstOpened) as? Date
        else { return false }
        return now.timeIntervalSince(firstOpened) >= Self.waitAfterFirstOpen
    }

    private static func day(_ date: Date) -> String {
        let parts = Calendar.current.dateComponents([.year, .month, .day], from: date)
        return "\(parts.year ?? 0)-\(parts.month ?? 0)-\(parts.day ?? 0)"
    }

    private enum Key {
        static let firstOpened = "reviewPrompt.firstOpened"
        static let watches = "reviewPrompt.watches"
        static let days = "reviewPrompt.days"
        static let askedVersion = "reviewPrompt.askedVersion"
    }
}
