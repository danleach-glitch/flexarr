import Foundation

/// Watching done from a download that hasn't reached the server yet, kept apart from the download (which signing out
/// deletes) until it's sent: only to the server and profile it was watched as, since the main profile has the same id
/// on every server. As Android's `UnsentReport`.
public struct UnsentReport: Codable, Hashable, Sendable {
    /// The server, as `UnsentReports.serverKey` writes its home address.
    public var server: String
    public var profileId: String
    public var itemId: String
    public var position: Double
    public var finished: Bool
    public var playedAt: Date
    /// A book's place, which goes to its progress rather than as offline progress.
    public var isBook: Bool
    /// How many times the server has said it has no such title (see `UnsentReports.notFoundAgain`).
    public var notFound: Int

    public init(server: String, profileId: String, itemId: String, position: Double, finished: Bool, playedAt: Date,
                isBook: Bool = false, notFound: Int = 0) {
        self.server = server
        self.profileId = profileId
        self.itemId = itemId
        self.position = position
        self.finished = finished
        self.playedAt = playedAt
        self.isBook = isBook
        self.notFound = notFound
    }

    /// The same title, watched as the same profile on the same server.
    func isFor(_ other: UnsentReport) -> Bool {
        server == other.server && profileId == other.profileId && itemId == other.itemId
    }
}

/// The rules for reports of watching done offline that are waiting to be sent.
public enum UnsentReports {
    /// A title the server says it hasn't (404) is tried this many times, on separate catch-ups, before its report is
    /// dropped: once could be a library mid-rescan, but every time for ever is a report that never goes.
    public static let notFoundLimit = 3

    /// How a server is told apart: its home address, without a trailing slash, its scheme and host in lower case.
    public static func serverKey(_ address: URL) -> String {
        var text = address.absoluteString
        while text.hasSuffix("/") { text.removeLast() }
        guard var parts = URLComponents(string: text) else { return text.lowercased() }
        parts.scheme = parts.scheme?.lowercased()
        parts.host = parts.host?.lowercased()
        return parts.string ?? text
    }

    /// `kept` with `new` added: one per title, server and profile, the one played most recently.
    public static func adding(_ new: [UnsentReport], to kept: [UnsentReport]) -> [UnsentReport] {
        var result = kept
        for report in new {
            if let index = result.firstIndex(where: { $0.isFor(report) }) {
                if report.playedAt >= result[index].playedAt { result[index] = report }
            } else {
                result.append(report)
            }
        }
        return result
    }

    /// The ones a sign-in to `server` as `profileId` sends: none while either isn't known.
    public static func due(in kept: [UnsentReport], server: String?, profileId: String?) -> [UnsentReport] {
        guard let server, let profileId else { return [] }
        return kept.filter { $0.server == server && $0.profileId == profileId }
    }

    /// `kept` without `report`, once it's been sent (or dropped): a newer one for the same title, recorded meanwhile, stays.
    public static func removing(_ report: UnsentReport, from kept: [UnsentReport]) -> [UnsentReport] {
        kept.filter { !($0.isFor(report) && $0.playedAt <= report.playedAt) }
    }

    /// The server said it has no such title, `count` times before: the count to keep, or nil once it's had its tries
    /// and the report goes.
    public static func notFoundAgain(_ count: Int) -> Int? {
        count + 1 >= notFoundLimit ? nil : count + 1
    }
}
