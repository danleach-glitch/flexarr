import Foundation

/// Which next episode a downloaded episode brings by itself (Settings › Download the next episode), as Android's
/// `DownloadRules.nextToFetch`.
public enum NextEpisodeRule {
    /// The episode after a downloaded one, to fetch by itself: nil when it isn't an episode that's here, has no next,
    /// or the next is one deleted by hand (`skipped`). One that was itself fetched this way is the episode ahead, and
    /// brings the one after only once it's been watched: otherwise each would bring the next, to the end of the show.
    public static func nextToFetch(kind: MediaKind, isDownloaded: Bool, automatic: Bool, watched: Bool, nextId: String?,
                                   skipped: Set<String> = []) -> String? {
        guard kind == .episode, isDownloaded, !automatic || watched, let nextId, !skipped.contains(nextId) else { return nil }
        return nextId
    }
}
