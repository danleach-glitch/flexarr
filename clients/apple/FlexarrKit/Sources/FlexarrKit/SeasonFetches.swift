import Foundation

/// The seasons a show's page is fetching, so each is asked for once. The page's load fetches the selected season and
/// the effect keyed on the selected season fires for the same change: that one waits for the fetch already out instead
/// of sending a second. A forced fetch (a reload, or Try again) always goes.
public struct SeasonFetches: Sendable, Equatable {
    public private(set) var inFlight: Set<String> = []

    public init() {}

    /// Whether to fetch season `id` now, marking it under way if so. Unforced, a season already had (`have`) or already
    /// being fetched is left alone.
    public mutating func start(_ id: String, have: Bool, force: Bool) -> Bool {
        if !force, have || inFlight.contains(id) { return false }
        inFlight.insert(id)
        return true
    }

    /// The fetch of `id` is over, however it ended.
    public mutating func finish(_ id: String) {
        inFlight.remove(id)
    }
}
