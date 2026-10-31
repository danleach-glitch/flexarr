import Foundation
import Observation

/// When playback should stop by itself: after so many minutes, or at the end of the episode. Podcasts (`MusicPlayer`)
/// and the video player each keep one; this is the part they share, the choice and the clock. What happens as it
/// runs out is theirs: a podcast just pauses, a video asks "Still watching?" first and fades out.
@MainActor
@Observable
final class SleepTimer {
    enum Setting: Hashable {
        case minutes(Int)
        case endOfEpisode
        /// A book's: the end of the chapter playing when it was set (the player notices it, as for an episode).
        case endOfChapter

        /// "15 minutes", "End of episode".
        var label: String {
            switch self {
            case .minutes(let minutes): "\(minutes) minutes"
            case .endOfEpisode: "End of episode"
            case .endOfChapter: "End of chapter"
            }
        }
    }

    /// The lengths offered, in minutes.
    static let minuteChoices = [15, 30, 45, 60]

    private(set) var setting: Setting?
    /// When a timer of minutes runs out.
    private(set) var endsAt: Date?
    /// A timer of minutes ran out (its setting is cleared first). End of episode is the player's to notice.
    @ObservationIgnored var onExpire: (() -> Void)?
    @ObservationIgnored private var task: Task<Void, Never>?

    /// Starts (or replaces, or with nil turns off) the timer. A timer of minutes counts from now.
    func set(_ setting: Setting?) {
        task?.cancel()
        task = nil
        self.setting = setting
        endsAt = nil
        guard case .minutes(let minutes) = setting else { return }
        let ends = Date().addingTimeInterval(Double(minutes * 60))
        endsAt = ends
        task = Task { [weak self] in
            try? await Task.sleep(for: .seconds(ends.timeIntervalSinceNow))
            guard !Task.isCancelled, let self else { return }
            self.task = nil
            self.setting = nil
            self.endsAt = nil
            self.onExpire?()
        }
    }

    /// Seconds until a timer of minutes runs out (nil for none, or end of episode).
    var remaining: TimeInterval? {
        endsAt.map { max(0, $0.timeIntervalSinceNow) }
    }
}
