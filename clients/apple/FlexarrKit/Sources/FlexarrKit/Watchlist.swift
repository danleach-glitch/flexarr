import Foundation

// MARK: - Models

/// Where a title on the watchlist has got to.
public enum WatchlistStatus: String, ForwardCompatibleEnum {
    /// In the library: play it.
    case ready = "Ready"
    /// Requested, or being fetched.
    case onItsWay = "OnItsWay"
    /// Not out yet (`releaseDate` says when).
    case coming = "Coming"
    /// Out, but nobody has asked for it: it can be requested.
    case notAvailable = "NotAvailable"
    /// A state from a newer server: shown without a label.
    case unknown
}

/// A film or show on the profile's watchlist, in the library or not. Absent from servers before 0.56.
public struct WatchlistEntry: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    /// "movie" or "tv".
    public let mediaType: String
    public let tmdbId: Int?
    public let title: String
    public let year: Int?
    public let poster: String?
    public let backdrop: String?
    public let addedAt: Date
    public let status: WatchlistStatus
    /// yyyy-MM-dd, for one that's coming.
    public let releaseDate: String?
    public let libraryItemId: String?
    /// The library title, when it's ready, for the usual poster card.
    public let item: ItemSummary?
    /// "flexarr", or "trakt" for one brought across from Trakt.
    public let source: String
    /// For a title not in the library, the subscription services it's on in the household's region (up to three).
    public var streaming: [String]? = nil

    public var isShow: Bool { mediaType == "tv" }

    /// The title as Discover knows it, to open its page or request it while it isn't in the library.
    public var external: ExternalTitle? {
        tmdbId.map {
            ExternalTitle(tmdbId: $0, mediaType: mediaType, title: title, year: year, overview: nil, poster: poster, backdrop: backdrop, rating: 0,
                          availability: status == .onItsWay ? .requested : .none, libraryItemId: libraryItemId, onWatchlist: true)
        }
    }
}

/// What's on the watchlist, cheaply: the library titles on it, and every title as "movie:123" / "tv:456".
public struct WatchlistKeys: Codable, Sendable, Hashable {
    public let libraryItemIds: [String]
    public let titles: [String]
}

/// What adding a title did: the entry, and whether it was also requested (the profile requests what it adds).
public struct WatchlistAdded: Codable, Sendable, Hashable {
    public let entry: WatchlistEntry
    public let requested: Bool
}

/// A title to add or take off: a library film or show (a season or episode stands for its show), or one by TMDB id.
public enum WatchlistKey: Sendable, Hashable {
    case library(String)
    case tmdb(mediaType: String, id: Int)

    public init(_ title: ExternalTitle) {
        self = title.libraryItemId.map(WatchlistKey.library) ?? .tmdb(mediaType: title.mediaType, id: title.tmdbId)
    }

    var query: [URLQueryItem] {
        switch self {
        case .library(let id): [.init(name: "libraryItemId", value: id)]
        case .tmdb(let mediaType, let id): [.init(name: "mediaType", value: mediaType), .init(name: "tmdbId", value: String(id))]
        }
    }
}

private struct WatchlistBody: Encodable {
    var libraryItemId: String?
    var mediaType: String?
    var tmdbId: Int?
}

extension Notification.Name {
    /// Posted when this app changes the watchlist, so lists and buttons elsewhere catch up.
    public static let watchlistChanged = Notification.Name("FlexarrWatchlistChanged")
}

// MARK: - Endpoints

extension FlexarrClient {
    /// What's on the profile's watchlist, without the server asking Seerr. Older servers answer this with the web
    /// app's page (200, text/html), which `send` refuses as `unexpectedResponse`: nil, no watchlist there. Network
    /// trouble throws, so a caller can keep what it knew.
    public func watchlistKeys() async throws -> WatchlistKeys? {
        do {
            return try await get("watchlist/keys")
        } catch FlexarrClientError.unexpectedResponse {
            return nil
        } catch FlexarrClientError.http(status: 404, _) {
            return nil
        }
    }

    /// The profile's watchlist, newest first.
    public func watchlist() async throws -> [WatchlistEntry] {
        try await get("watchlist")
    }

    @discardableResult
    public func addToWatchlist(_ key: WatchlistKey) async throws -> WatchlistAdded {
        let body = switch key {
        case .library(let id): WatchlistBody(libraryItemId: id)
        case .tmdb(let mediaType, let id): WatchlistBody(mediaType: mediaType, tmdbId: id)
        }
        let added = try decode(WatchlistAdded.self, from: try await send("POST", "watchlist", body: body))
        await MainActor.run { NotificationCenter.default.post(name: .watchlistChanged, object: nil) }
        return added
    }

    public func removeFromWatchlist(_ key: WatchlistKey) async throws {
        _ = try await send("DELETE", "watchlist", query: key.query)
        await MainActor.run { NotificationCenter.default.post(name: .watchlistChanged, object: nil) }
    }
}
