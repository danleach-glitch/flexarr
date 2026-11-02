import Foundation

// MARK: - A show's gaps

/// A season of a show that the library has none of.
public struct MissingSeason: Codable, Sendable, Hashable, Identifiable {
    public let seasonNumber: Int
    public let name: String?
    /// How many of its episodes have aired.
    public let episodes: Int
    public let year: Int?
    public let poster: String?
    /// Requested when Sonarr is already looking for it (or Seerr says so).
    public let availability: Availability

    public var id: Int { seasonNumber }
    public var title: String { name ?? "Season \(seasonNumber)" }

    public init(seasonNumber: Int, name: String?, episodes: Int, year: Int?, poster: String?, availability: Availability) {
        self.seasonNumber = seasonNumber
        self.name = name
        self.episodes = episodes
        self.year = year
        self.poster = poster
        self.availability = availability
    }
}

/// An episode that aired and isn't in the library, or one still to air.
public struct ShowEpisode: Codable, Sendable, Hashable, Identifiable {
    public let seasonNumber: Int
    public let episodeNumber: Int
    public let title: String?
    public let airDate: Date?
    public let still: String?
    /// Sonarr is looking for it.
    public let monitored: Bool

    public var id: String { "S\(seasonNumber)E\(episodeNumber)" }
    /// "S2E4".
    public var code: String { id }

    public init(seasonNumber: Int, episodeNumber: Int, title: String?, airDate: Date?, still: String?, monitored: Bool) {
        self.seasonNumber = seasonNumber
        self.episodeNumber = episodeNumber
        self.title = title
        self.airDate = airDate
        self.still = still
        self.monitored = monitored
    }

    /// "Airs 12 Nov", "Airs 3 Jan 2028" (a year only when it isn't this one), or "Airs today".
    public func airs(now: Date = Date(), calendar: Calendar = .current, locale: Locale = .current) -> String? {
        guard let airDate else { return nil }
        if calendar.isDate(airDate, inSameDayAs: now) { return "Airs today" }
        let style = calendar.component(.year, from: airDate) == calendar.component(.year, from: now)
            ? Date.FormatStyle(date: .omitted, time: .omitted).day().month(.abbreviated)
            : Date.FormatStyle(date: .omitted, time: .omitted).day().month(.abbreviated).year()
        return "Airs " + airDate.formatted(style.locale(locale))
    }
}

/// Of a season the library has: how many of its aired episodes it has.
public struct SeasonCount: Codable, Sendable, Hashable {
    public let seasonNumber: Int
    public let have: Int
    public let aired: Int

    public init(seasonNumber: Int, have: Int, aired: Int) {
        self.seasonNumber = seasonNumber
        self.have = have
        self.aired = aired
    }

    /// "8 of 10" when some aired episodes are missing; nil when the season is whole.
    public var label: String? { have < aired ? "\(have) of \(aired)" : nil }
}

/// What a show has that the library doesn't, and what's coming. Absent from servers before 0.59 (no section there).
public struct ShowGaps: Codable, Sendable, Hashable {
    /// "sonarr" (seasons and episodes), "seerr" (whole seasons only) or "none".
    public let source: String
    /// This profile can request a missing season through Seerr.
    public let canRequest: Bool
    /// This profile can ask Sonarr to search for missing episodes (the main profile, for a show Sonarr manages).
    public let canSearch: Bool
    public let seasons: [MissingSeason]
    /// Aired episodes missing from seasons the library has some of.
    public let episodes: [ShowEpisode]
    /// The next episodes Sonarr has air dates for, up to five.
    public let upcoming: [ShowEpisode]
    public let counts: [SeasonCount]

    public init(source: String, canRequest: Bool, canSearch: Bool, seasons: [MissingSeason], episodes: [ShowEpisode], upcoming: [ShowEpisode],
                counts: [SeasonCount]) {
        self.source = source
        self.canRequest = canRequest
        self.canSearch = canSearch
        self.seasons = seasons
        self.episodes = episodes
        self.upcoming = upcoming
        self.counts = counts
    }

    public static let none = ShowGaps(source: "none", canRequest: false, canSearch: false, seasons: [], episodes: [], upcoming: [], counts: [])

    /// Whether there's anything for a *Not in your library* section.
    public var hasMissing: Bool { !seasons.isEmpty || !episodes.isEmpty }

    /// "8 of 10" for a season the library has some of; nil when it's whole or unknown.
    public func countLabel(season: Int?) -> String? {
        guard let season else { return nil }
        return counts.first { $0.seasonNumber == season }?.label
    }

    /// Missing episodes, a season at a time, oldest first.
    public var episodesBySeason: [(season: Int, episodes: [ShowEpisode])] {
        Dictionary(grouping: episodes, by: \.seasonNumber).sorted { $0.key < $1.key }.map { ($0.key, $0.value) }
    }

    /// What a missing season's button does for this profile.
    public func action(for season: MissingSeason) -> MissingSeasonAction {
        if season.availability == .requested || season.availability == .partlyAvailable { return .requested }
        if canRequest { return .request }
        if canSearch && source == "sonarr" { return .search }
        return .none
    }
}

/// A missing season's button: Request (Seerr), Search (Sonarr, the main profile), greyed Requested, or nothing.
public enum MissingSeasonAction: Sendable, Hashable {
    case request
    case search
    case requested
    case none
}

// MARK: - A book series' gaps

/// A book of a series that the library doesn't have, as Audible lists it.
public struct MissingBook: Codable, Sendable, Hashable, Identifiable {
    public let asin: String
    public let title: String
    /// Its number in the series as written ("3", "1.5"); nil when Audible gives none.
    public let sequence: String?
    public let authors: [String]
    public let year: Int?
    public let cover: String?

    public var id: String { asin }
    /// "Book 3 · 2021".
    public var caption: String? {
        let parts = [sequence.map { "Book \($0)" }, year.map(String.init)].compactMap(\.self)
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    public init(asin: String, title: String, sequence: String?, authors: [String], year: Int?, cover: String?) {
        self.asin = asin
        self.title = title
        self.sequence = sequence
        self.authors = authors
        self.year = year
        self.cover = cover
    }
}

/// The books Audible lists in a series that the library lacks. Absent from servers before 0.59 (no section there).
public struct BookSeriesGaps: Codable, Sendable, Hashable {
    public let books: [MissingBook]
    /// False when the series has no Audible id or Audible couldn't be asked.
    public let answered: Bool

    public static let none = BookSeriesGaps(books: [], answered: false)

    public init(books: [MissingBook], answered: Bool) {
        self.books = books
        self.answered = answered
    }
}

private struct SeasonsRequest: Encodable {
    let mediaType = "tv"
    let tmdbId: Int
    let seasons: [Int]
}

private struct MissingSearch: Encodable {
    let seasonNumber: Int
    let episodeNumbers: [Int]?
}

// MARK: - Endpoints

extension FlexarrClient {
    /// A show's missing seasons and episodes, what's coming, and its season counts. Servers before 0.59 answer 404: none.
    public func showGaps(_ showId: String) async throws -> ShowGaps {
        do {
            return try await get("items/\(showId)/missing")
        } catch FlexarrClientError.http(status: 404, _) {
            return .none
        } catch FlexarrClientError.unexpectedResponse {
            return .none
        }
    }

    /// Asks Seerr for just these seasons of a show, by its TMDB id.
    public func requestSeasons(tmdbId: Int, seasons: [Int]) async throws {
        _ = try await send("POST", "requests", body: SeasonsRequest(tmdbId: tmdbId, seasons: seasons))
    }

    /// Asks Sonarr to look for a season's missing episodes (the main profile); all of them, or just `episodes`.
    public func searchMissing(showId: String, season: Int, episodes: [Int]? = nil) async throws {
        _ = try await send("POST", "items/\(showId)/missing/search", body: MissingSearch(seasonNumber: season, episodeNumbers: episodes))
    }

    /// The books Audible lists in a series that the library lacks. Servers before 0.59 answer 404: none.
    public func bookSeriesGaps(_ seriesId: String) async throws -> BookSeriesGaps {
        do {
            return try await get("book-series/\(seriesId)/missing")
        } catch FlexarrClientError.http(status: 404, _) {
            return .none
        } catch FlexarrClientError.unexpectedResponse {
            return .none
        }
    }
}
