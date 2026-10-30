import Foundation

// MARK: - Models

/// Whether requests are set up: a Seerr server is configured, and answered just now.
public struct RequestStatus: Codable, Sendable, Hashable {
    public let configured: Bool
    public let reachable: Bool
}

/// Where a title from outside the library stands.
public enum Availability: String, ForwardCompatibleEnum {
    /// Not in the library and not requested.
    case none = "None"
    case requested = "Requested"
    case partlyAvailable = "PartlyAvailable"
    case available = "Available"
    /// A state from a newer server: shown as not requestable.
    case unknown
}

/// A film or show TMDB knows (via Seerr), with whether the library has it.
public struct ExternalTitle: Codable, Sendable, Hashable, Identifiable {
    public let tmdbId: Int
    /// "movie" or "tv".
    public let mediaType: String
    public let title: String
    public let year: Int?
    public let overview: String?
    public let poster: String?
    public let backdrop: String?
    public let rating: Double
    public let availability: Availability
    /// Set when it's in the library: it opens as itself.
    public let libraryItemId: String?
    /// Whether it's on the profile's watchlist (Discover and search). Absent from servers before 0.56.
    public var onWatchlist: Bool? = nil

    public var id: String { "\(mediaType)-\(tmdbId)" }
    public var isShow: Bool { mediaType == "tv" }

    public init(tmdbId: Int, mediaType: String, title: String, year: Int?, overview: String?, poster: String?, backdrop: String?,
                rating: Double, availability: Availability, libraryItemId: String?, onWatchlist: Bool? = nil) {
        self.tmdbId = tmdbId
        self.mediaType = mediaType
        self.title = title
        self.year = year
        self.overview = overview
        self.poster = poster
        self.backdrop = backdrop
        self.rating = rating
        self.availability = availability
        self.libraryItemId = libraryItemId
        self.onWatchlist = onWatchlist
    }
}

/// One of Discover's rows: trending, popular films, popular shows, coming to cinemas.
public struct ExternalRow: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let title: String
    public let items: [ExternalTitle]
}

public struct ExternalPerson: Codable, Sendable, Hashable {
    public let name: String
    public let character: String?
    public let image: String?
}

/// Everything worth knowing about a film or show the library doesn't have, from Seerr (which gets it from TMDB).
public struct ExternalDetail: Codable, Sendable, Hashable {
    public let tmdbId: Int
    public let mediaType: String
    public let title: String
    public let tagline: String?
    public let year: Int?
    public let overview: String?
    public let poster: String?
    public let backdrop: String?
    public let genres: [String]
    public let runtimeMinutes: Int?
    public let seasons: Int?
    public let certification: String?
    public let status: String?
    public let tmdbRating: Double?
    /// Rotten Tomatoes critics, percent.
    public let criticScore: Int?
    /// Rotten Tomatoes audience, percent.
    public let audienceScore: Int?
    public let imdbRating: Double?
    public let cast: [ExternalPerson]
    /// Directors of a film, creators of a show.
    public let madeBy: [String]
    /// A film's studio, a show's network.
    public let studio: String?
    public let availability: Availability
    public let libraryItemId: String?
    public let recommendations: [ExternalTitle]
    /// Whether it's on the profile's watchlist. Absent from servers before 0.56 (no watchlist there).
    public var onWatchlist: Bool? = nil

    public var isShow: Bool { mediaType == "tv" }

    /// The title as Discover's rows know it, for showing the page's header before the rest arrives.
    public var summary: ExternalTitle {
        ExternalTitle(tmdbId: tmdbId, mediaType: mediaType, title: title, year: year, overview: overview, poster: poster, backdrop: backdrop,
                      rating: tmdbRating ?? 0, availability: availability, libraryItemId: libraryItemId, onWatchlist: onWatchlist)
    }
}

/// A genre in the library, with how many films and shows are in it and a backdrop to show it by.
public struct Genre: Codable, Sendable, Hashable, Identifiable {
    public let name: String
    public let movies: Int
    public let shows: Int
    public let backdrop: String?

    public var id: String { name }
}

private struct TitleRequest: Encodable {
    let mediaType: String
    let tmdbId: Int
}

// MARK: - Endpoints

extension FlexarrClient {
    /// Whether this server can take requests. Servers before requests answer 404: no.
    public func requestStatus() async throws -> RequestStatus {
        do {
            return try await get("requests/status")
        } catch FlexarrClientError.http(status: 404, _) {
            return RequestStatus(configured: false, reachable: false)
        }
    }

    /// Discover's rows from Seerr; none without a Seerr server.
    public func discoverRows() async throws -> [ExternalRow] {
        try await get("requests/discover")
    }

    /// Any film or show TMDB knows, not just the library's, marked if the library has it.
    public func searchEverything(_ query: String) async throws -> [ExternalTitle] {
        try await get("requests/search", query: [.init(name: "q", value: query)])
    }

    public func externalTitle(_ title: ExternalTitle) async throws -> ExternalDetail {
        try await get("requests/title/\(title.mediaType)/\(title.tmdbId)")
    }

    /// Asks Seerr for the title, and the server remembers who asked so they hear when it's in.
    public func request(_ title: ExternalTitle) async throws {
        _ = try await send("POST", "requests", body: TitleRequest(mediaType: title.mediaType, tmdbId: title.tmdbId))
    }

    /// The library's genres, biggest first.
    public func genres() async throws -> [Genre] {
        try await get("genres")
    }

    /// What the library has in a genre, best rated first; `kind` narrows it to films or shows.
    public func genreItems(_ name: String, kind: MediaKind? = nil, take: Int = 120) async throws -> Paged<ItemSummary> {
        var query: [URLQueryItem] = [.init(name: "take", value: String(take))]
        if let kind { query.append(.init(name: "kind", value: kind.rawValue)) }
        // One segment, whatever's in the name; the rest of it is percent-encoded as the request is made ("Science
        // Fiction", "Action & Adventure").
        return try await get("genres/\(Self.pathSegment(name))/items", query: query)
    }
}
