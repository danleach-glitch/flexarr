import Foundation

// Trakt, per profile: connect by a code entered at trakt.tv/activate, how the sync is going, the first import, and
// disconnecting. Absent from servers before 0.57: `traktStatus()` is nil there, and the apps show nothing.

public enum TraktConnectState: String, ForwardCompatibleEnum {
    case waiting = "Waiting"
    case connected = "Connected"
    case expired = "Expired"
    case denied = "Denied"
    case inUse = "InUse"
    case failed = "Failed"
    case unknown
}

public enum TraktAccountState: String, ForwardCompatibleEnum {
    case disconnected
    case connected
    case needsSignIn
    case unknown
}

/// A connect under way (the code to enter, and where), or how one just ended.
public struct TraktPendingConnect: Codable, Sendable, Hashable {
    public let state: TraktConnectState
    public let userCode: String
    public let verificationUrl: String
    /// What to put in a QR code: Trakt's activation page.
    public let qrUrl: String
    public let expiresAt: Date?
    public var error: String? = nil
}

public struct TraktStatus: Codable, Sendable, Hashable {
    /// The server can offer Trakt at all.
    public let available: Bool
    public let state: TraktAccountState
    public var username: String? = nil
    public var lastSyncAt: Date? = nil
    public var lastError: String? = nil
    public let watchlistFull: Bool
    public var importedAt: Date? = nil
    public let importing: Bool
    public let queued: Int
    public var pending: TraktPendingConnect? = nil
}

/// What the first history import would do, shown before it's confirmed.
public struct TraktImportPreview: Codable, Sendable, Hashable {
    public let traktFilms: Int
    public let traktEpisodes: Int
    public let inLibrary: Int
    public let newToFlexarr: Int
    public let notOnServer: Int
    public let unmatched: Int
    public let toTrakt: Int

    public init(traktFilms: Int, traktEpisodes: Int, inLibrary: Int, newToFlexarr: Int, notOnServer: Int, unmatched: Int, toTrakt: Int) {
        self.traktFilms = traktFilms
        self.traktEpisodes = traktEpisodes
        self.inLibrary = inLibrary
        self.newToFlexarr = newToFlexarr
        self.notOnServer = notOnServer
        self.unmatched = unmatched
        self.toTrakt = toTrakt
    }

    /// "Trakt has 12 films and 340 episodes".
    public var summary: String {
        "Trakt has \(Self.count(traktFilms, "film")) and \(Self.count(traktEpisodes, "episode"))"
    }

    /// What changes, in a line or two.
    public var details: String {
        var parts = [inLibrary == 0 ? "None are here yet"
            : "\(inLibrary.formatted()) are here" + (newToFlexarr > 0 ? ", \(newToFlexarr.formatted()) not yet watched in Flexarr" : "")]
        if notOnServer > 0 { parts.append("\(notOnServer.formatted()) will show as watched if they arrive") }
        if toTrakt > 0 { parts.append("\(Self.count(toTrakt, "play")) from Flexarr \(toTrakt == 1 ? "goes" : "go") to Trakt") }
        return parts.joined(separator: ". ") + "."
    }

    static func count(_ n: Int, _ one: String) -> String { "\(n.formatted()) \(n == 1 ? one : one + "s")" }
}

extension TraktConnectState {
    /// Why a connect ended without connecting, in a few words; nil while waiting or once connected.
    public var problem: String? {
        switch self {
        case .waiting, .connected: nil
        case .expired: "The code ran out. Try again."
        case .denied: "Trakt said no."
        case .inUse: "That Trakt account is connected to another profile."
        case .failed, .unknown: "That didn’t work. Try again."
        }
    }
}

/// One of the profile's Trakt lists (its own, or one it liked).
public struct TraktList: Codable, Sendable, Hashable, Identifiable {
    /// "{user}/{list}": what saving the choice takes.
    public let id: String
    public let name: String
    /// Whose it is, for a list liked on Trakt.
    public var owner: String? = nil
    public let itemCount: Int
    public let liked: Bool
    public let selected: Bool

    /// "Sam · 12 titles", or "12 titles" for the profile's own.
    public var detail: String {
        let titles = TraktImportPreview.count(itemCount, "title")
        return owner.map { "\($0) · \(titles)" } ?? titles
    }
}

/// The words every app uses for Trakt, so they say the same thing.
public enum TraktWords {
    public static let waiting = "Flexarr waits, so you can leave this."
    public static let importEntry = "Bring your Trakt history across"
    public static let importAction = "Bring across"
    public static let importing = "Bringing your history across…"
    public static let imported = "History brought across"
    public static let watchlistFull = "Trakt’s watchlist is full. New titles stay on Flexarr’s list."
    public static let signedOut = "Trakt signed Flexarr out."
    public static let listsNote = "Chosen lists show in Collections, just for you."
    public static let disconnectTitle = "Disconnect Trakt?"
    public static let disconnectMessage = "What you’ve watched stays as it is, here and on Trakt."
    public static let unavailable = "Trakt isn’t available on this server."
    public static let failed = "That didn’t work. Try again."
    public static let unreachable = "Couldn’t reach the server."

    /// "Enter this code at trakt.tv/activate."
    public static func enterCode(at url: String) -> String {
        "Enter this code at \(url.replacingOccurrences(of: "https://", with: ""))."
    }

    /// "Synced 5 minutes ago", or "Not synced yet".
    public static func synced(_ date: Date?, now: Date = .now) -> String {
        guard let date else { return "Not synced yet" }
        return "Synced \(date.formatted(.relative(presentation: .named, unitsStyle: .wide)))"
    }

    /// The server's own words for what went wrong, when it gave some.
    public static func problem(_ error: Error) -> String {
        if case FlexarrClientError.http(_, let message?) = error, !message.isEmpty { return message }
        if error is URLError { return unreachable }
        return failed
    }
}

// MARK: - Endpoints

extension FlexarrClient {
    /// Nil from servers without Trakt (before 0.57), and on kids profiles.
    public func traktStatus() async throws -> TraktStatus? {
        do {
            return try await get("trakt/status")
        } catch FlexarrClientError.unexpectedResponse {
            return nil
        } catch FlexarrClientError.http(status: 404, _), FlexarrClientError.http(status: 403, _) {
            return nil
        }
    }

    public func connectTrakt() async throws -> TraktPendingConnect {
        try decode(TraktPendingConnect.self, from: try await send("POST", "trakt/connect"))
    }

    public func cancelTraktConnect() async throws {
        _ = try await send("DELETE", "trakt/connect")
    }

    public func disconnectTrakt() async throws {
        _ = try await send("DELETE", "trakt")
    }

    public func previewTraktImport() async throws -> TraktImportPreview {
        try decode(TraktImportPreview.self, from: try await send("POST", "trakt/import"))
    }

    public func confirmTraktImport() async throws {
        _ = try await send("POST", "trakt/import", query: [.init(name: "confirm", value: "true")])
    }

    public func traktLists() async throws -> [TraktList] {
        try await get("trakt/lists")
    }

    public func saveTraktLists(_ ids: [String]) async throws {
        _ = try await send("PUT", "trakt/lists", body: TraktListsBody(ids: ids))
    }

    public func syncTraktNow() async throws {
        _ = try await send("POST", "trakt/sync")
    }
}

private struct TraktListsBody: Encodable {
    let ids: [String]
}
