import Foundation

/// Which of the downloads the server is getting ready a phone asks after on each look (every few seconds). The server
/// works through them in the order they were asked for, so only the first few are likely to have moved since the last
/// look: those are asked every time, and the rest a couple at a time, each at most every `othersEvery`, so a whole
/// show waiting (a hundred episodes and more) costs the server a handful of questions a look rather than one each.
public enum DownloadPolling {
    /// How many of the first waiting are asked after on every look.
    public static let everyLook = 3
    /// How many of the rest are asked after on one look, those asked longest ago first.
    public static let othersPerLook = 2
    /// The least time between two questions about one of the rest.
    public static let othersEvery: TimeInterval = 30

    /// The ids to ask about now. `waiting`: in the order they were asked for. `lastAsked`: when each was last asked
    /// about (or heard of: one not in it counts as just asked, since it was answered when it was asked for).
    public static func due(_ waiting: [String], lastAsked: [String: Date], now: Date = Date()) -> [String] {
        let first = Array(waiting.prefix(everyLook))
        let others = waiting.dropFirst(everyLook)
            .compactMap { id in lastAsked[id].map { (id: id, at: $0) } }
            .filter { now.timeIntervalSince($0.at) >= othersEvery }
            .sorted { $0.at < $1.at }
            .prefix(othersPerLook)
            .map(\.id)
        return first + others
    }
}
