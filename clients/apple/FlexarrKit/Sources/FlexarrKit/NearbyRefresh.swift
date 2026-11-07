import Foundation

/// After something is watched or marked, a long library grid fetches again only the titles around where it's looking
/// rather than every title it has loaded (audit perf-71): one request instead of one per 500 titles. It's patched in
/// place only when that stretch comes back as the same titles in the same order, of a library the same size; anything
/// else (a title added, removed or renamed into another place) has the grid fetch everything as before.
public enum NearbyRefresh {
    /// The titles either side of the one last shown that are fetched again.
    public static let margin = 120

    /// Where a grid was last looking: the index of a title that came on screen, or the id of the one with focus.
    /// A class, kept outside the view's state, so noting it as titles come and go doesn't draw the grid again.
    @MainActor public final class Place {
        public var index = 0
        public var id: String?
        public init() {}
    }

    /// The stretch of `loaded` titles to fetch again around `index`, or nil when that would be all of them anyway.
    public static func window(around index: Int, loaded: Int, margin: Int = margin) -> Range<Int>? {
        guard loaded > 2 * margin else { return nil }
        let centre = min(max(0, index), loaded - 1)
        let lower = max(0, min(centre - margin, loaded - 2 * margin))
        return lower..<(lower + 2 * margin)
    }

    /// `items` with `window` replaced by `fetched`, or nil when the fetched stretch isn't the same titles in the same
    /// order (or the library's size changed), so the whole list should be fetched again.
    public static func patch<Item: Identifiable>(_ items: [Item], window: Range<Int>, fetched: [Item],
                                                 fetchedTotal: Int, total: Int) -> [Item]? {
        guard fetchedTotal == total, window.lowerBound >= 0, window.upperBound <= items.count,
              fetched.count == window.count,
              fetched.map(\.id) == items[window].map(\.id) else { return nil }
        var patched = items
        patched.replaceSubrange(window, with: fetched)
        return patched
    }
}
