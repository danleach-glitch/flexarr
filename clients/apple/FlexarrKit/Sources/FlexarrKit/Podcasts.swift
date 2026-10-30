import Foundation

// MARK: - Models

/// Whether this profile has podcasts. Every server has one Podcasts library, so `available` is false only when it's
/// hidden from this profile. `country` is whose charts Browse shows.
public struct PodcastStatus: Codable, Sendable, Hashable {
    public let available: Bool
    public let country: String
}

/// This profile follows the show (since when), and whether it listens from the first episode on.
public struct PodcastSubscription: Codable, Sendable, Hashable {
    public let subscribedAt: Date
    /// Episodes in the order they came out (a story told in parts): the show lists, plays and downloads its oldest
    /// unplayed ones first. Absent from servers before it existed.
    public var oldestFirst: Bool? = nil
}

/// How a profile listens to podcasts, on every device: whether phones keep the newest `keepEpisodes` unplayed
/// episodes of each show followed (on Wi-Fi), and the speed episodes play at.
public struct PodcastPreferences: Codable, Sendable, Hashable {
    public var autoDownload: Bool
    public var keepEpisodes: Int
    public var speed: Double

    public init(autoDownload: Bool = true, keepEpisodes: Int = 3, speed: Double = 1) {
        self.autoDownload = autoDownload
        self.keepEpisodes = keepEpisodes
        self.speed = speed
    }
}

/// Changes to the podcast preferences; nil leaves one as it is.
public struct PodcastPreferencesUpdate: Codable, Sendable, Hashable {
    public var autoDownload: Bool?
    public var keepEpisodes: Int?
    public var speed: Double?

    public init(autoDownload: Bool? = nil, keepEpisodes: Int? = nil, speed: Double? = nil) {
        self.autoDownload = autoDownload
        self.keepEpisodes = keepEpisodes
        self.speed = speed
    }
}

public struct Podcast: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let title: String
    public let author: String?
    public let overview: String?
    public let artwork: Artwork
    public let accentColor: String?
    public let link: String?
    public let categories: [String]
    public let explicit: Bool
    public let episodeCount: Int
    public let unplayedCount: Int
    public let latestEpisodeAt: Date?
    public let iTunesId: Int64?
    public let feedUrl: String
    public let checkedAt: Date?
    /// Why the feed couldn't be fetched last time, in words for the listener.
    public let error: String?
    /// Nil when this profile doesn't follow it.
    public let subscription: PodcastSubscription?

    public var isSubscribed: Bool { subscription != nil }

    /// The same show with another count of unplayed episodes (a change shown before the server confirms it).
    public func with(unplayedCount: Int) -> Podcast {
        Podcast(id: id, title: title, author: author, overview: overview, artwork: artwork, accentColor: accentColor, link: link,
                categories: categories, explicit: explicit, episodeCount: episodeCount, unplayedCount: max(0, unplayedCount),
                latestEpisodeAt: latestEpisodeAt, iTunesId: iTunesId, feedUrl: feedUrl, checkedAt: checkedAt, error: error,
                subscription: subscription)
    }

    /// The show as screens that navigate by title know it.
    public var summary: ItemSummary {
        ItemSummary(id: id, libraryId: "", parentId: nil, kind: .podcast, title: title, year: nil, seasonNumber: nil, episodeNumber: nil,
                    episodeNumberEnd: nil, airDate: nil, childCount: episodeCount, durationSeconds: nil, resolution: nil,
                    addedAt: latestEpisodeAt ?? Date(timeIntervalSince1970: 0), artwork: artwork, accentColor: accentColor,
                    communityRating: nil, criticRating: nil, contentRating: explicit ? "Explicit" : nil, runtimeMinutes: nil, releaseDate: nil,
                    userData: UserData(positionSeconds: 0, watched: unplayedCount == 0, playCount: 0, lastPlayedAt: nil, unwatchedCount: unplayedCount),
                    seriesTitle: author)
    }
}

public enum PodcastEpisodeType: String, ForwardCompatibleEnum {
    case full = "Full", trailer = "Trailer", bonus = "Bonus", unknown
}

public struct PodcastEpisode: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let podcastId: String
    public let podcastTitle: String
    public let title: String
    /// The episode's notes as plain text: cut short in lists, whole from `podcastEpisode(_:)`.
    public let overview: String?
    public let publishedAt: Date?
    public let durationSeconds: Double?
    public let season: Int?
    public let episode: Int?
    public let episodeType: PodcastEpisodeType
    public let explicit: Bool
    public let artwork: Artwork
    public let accentColor: String?
    /// `watched` means played.
    public let userData: UserData
    /// As the feed gives it, for showing what a download will take.
    public let sizeBytes: Int64?
    public let hasTranscript: Bool

    public var isPlayed: Bool { userData.watched }

    /// The same episode marked played (from the start) or unplayed: a change shown before the server confirms it.
    public func with(played: Bool) -> PodcastEpisode {
        PodcastEpisode(id: id, podcastId: podcastId, podcastTitle: podcastTitle, title: title, overview: overview, publishedAt: publishedAt,
                       durationSeconds: durationSeconds, season: season, episode: episode, episodeType: episodeType, explicit: explicit,
                       artwork: artwork, accentColor: accentColor,
                       userData: UserData(positionSeconds: 0, watched: played, playCount: played ? max(1, userData.playCount) : userData.playCount,
                                          lastPlayedAt: userData.lastPlayedAt, unwatchedCount: nil),
                       sizeBytes: sizeBytes, hasTranscript: hasTranscript)
    }

    /// Listened to part of the way (and not finished).
    public var progress: Double? {
        guard !userData.watched, userData.positionSeconds > 0, let duration = durationSeconds, duration > 0 else { return nil }
        return min(1, userData.positionSeconds / duration)
    }

    /// The episode as the player, downloads and Now Playing know titles: a summary whose show is the podcast.
    public var summary: ItemSummary {
        ItemSummary(id: id, libraryId: "", parentId: podcastId, kind: .podcastEpisode, title: title,
                    year: publishedAt.map { Calendar(identifier: .gregorian).component(.year, from: $0) },
                    seasonNumber: season, episodeNumber: episode, episodeNumberEnd: nil, airDate: nil, childCount: 0,
                    durationSeconds: durationSeconds, resolution: nil, addedAt: publishedAt ?? Date(), artwork: artwork,
                    accentColor: accentColor, communityRating: nil, criticRating: nil, contentRating: explicit ? "Explicit" : nil,
                    runtimeMinutes: durationSeconds.map { Int(($0 / 60).rounded()) }, releaseDate: nil, userData: userData,
                    seriesTitle: podcastTitle, seriesId: podcastId)
    }
}

/// A named part of an episode: from the feed's chapters file, or the times its notes list.
public struct PodcastChapter: Codable, Sendable, Hashable {
    public let startSeconds: Double
    public let title: String

    public init(startSeconds: Double, title: String) {
        self.startSeconds = startSeconds
        self.title = title
    }
}

extension [PodcastChapter] {
    /// Where the chapter playing at `position` is in the list; nil before the first starts.
    public func index(at position: Double) -> Int? {
        lastIndex { $0.startSeconds <= position + 0.5 }
    }
}

/// A show in Apple's podcast directory (search and the charts), and whether it's in the library already.
public struct DirectoryPodcast: Codable, Sendable, Hashable, Identifiable {
    public let iTunesId: Int64
    public let title: String
    public let author: String?
    /// Relative to the server, like other artwork.
    public let artwork: String?
    public let genre: String?
    public let explicit: Bool
    public let episodeCount: Int?
    /// Apple gives a feed for it, so it can be added.
    public let hasFeed: Bool
    public let podcastId: String?
    public let subscribed: Bool

    public var id: Int64 { iTunesId }
}

public struct PodcastGenre: Codable, Sendable, Hashable, Identifiable {
    public let id: Int
    public let name: String
}

/// An episode the phone should have without being asked, from a show it auto-downloads.
public struct PodcastAutoDownload: Codable, Sendable, Hashable {
    public let episodeId: String
    public let podcastId: String
}

/// An episode this phone has downloaded that's since been played (on another device, say): its copy can go.
public struct PodcastPlayedDownload: Codable, Sendable, Hashable {
    public let episodeId: String
}

public enum PodcastEpisodeSort: String, Sendable {
    case newest, oldest
}

struct UpNextBody: Codable, Sendable {
    let episodeId: String
    let next: Bool
}

struct UpNextOrderBody: Codable, Sendable {
    let episodeIds: [String]
}

struct SubscriptionBody: Codable, Sendable {
    let oldestFirst: Bool?
}

struct AddPodcastBody: Codable, Sendable {
    let feedUrl: String?
    let iTunesId: Int64?
}

// MARK: - Endpoints

extension FlexarrClient {
    /// Whether podcasts are set up for this profile. Servers before podcasts answer 404: none.
    public func podcastStatus() async throws -> PodcastStatus? {
        do {
            return try await get("podcasts/status")
        } catch FlexarrClientError.http(status: 404, _) {
            return nil
        }
    }

    /// The shows this profile follows, the most recently updated first.
    public func podcasts() async throws -> [Podcast] {
        try await get("podcasts")
    }

    public func podcast(_ id: String) async throws -> Podcast {
        try await get("podcasts/\(id)")
    }

    /// Adds a show to the library (or finds it there) by Apple's id or its feed's address. Doesn't follow it.
    public func addPodcast(iTunesId: Int64? = nil, feedUrl: String? = nil) async throws -> Podcast {
        let data = try await send("POST", "podcasts", body: AddPodcastBody(feedUrl: feedUrl, iTunesId: iTunesId))
        return try decode(Podcast.self, from: data)
    }

    public func podcastEpisodes(_ podcastId: String, sort: PodcastEpisodeSort = .newest, unplayed: Bool = false,
                                skip: Int = 0, take: Int = 50) async throws -> Paged<PodcastEpisode> {
        var query: [URLQueryItem] = [.init(name: "limit", value: String(take))]
        if skip > 0 { query.append(.init(name: "offset", value: String(skip))) }
        if sort != .newest { query.append(.init(name: "sort", value: sort.rawValue)) }
        if unplayed { query.append(.init(name: "unplayed", value: "true")) }
        return try await get("podcasts/\(podcastId)/episodes", query: query)
    }

    /// One episode with all of its notes.
    public func podcastEpisode(_ id: String) async throws -> PodcastEpisode {
        try await get("podcasts/episodes/\(id)")
    }

    /// The episode's chapters, in order; none when it has none. Absent from servers before 0.54, which have none.
    public func podcastChapters(_ episodeId: String) async throws -> [PodcastChapter] {
        do {
            return try await get("podcasts/episodes/\(episodeId)/chapters")
        } catch FlexarrClientError.http(status: 404, _) {
            return []
        }
    }

    /// The newest episodes of the shows this profile follows.
    public func newPodcastEpisodes(unplayed: Bool = false, take: Int = 40) async throws -> [PodcastEpisode] {
        var query: [URLQueryItem] = [.init(name: "limit", value: String(take))]
        if unplayed { query.append(.init(name: "unplayed", value: "true")) }
        return try await get("podcasts/new", query: query)
    }

    /// Episodes this profile is part way through, the latest first.
    public func continueListening(take: Int = 20) async throws -> [PodcastEpisode] {
        try await get("podcasts/continue", query: [.init(name: "limit", value: String(take))])
    }

    /// Follows a show, or (with `oldestFirst`) changes the order it's listened in. Returns the show with its subscription.
    public func subscribe(to podcastId: String, oldestFirst: Bool? = nil) async throws -> Podcast {
        let data = try await send("PUT", "podcasts/\(podcastId)/subscription", body: SubscriptionBody(oldestFirst: oldestFirst))
        return try decode(Podcast.self, from: data)
    }

    /// What to play when this episode ends: the next unplayed one of its show, else the newest unplayed one of the
    /// shows followed. Nil when there's nothing left (or the server is from before auto-play).
    public func nextEpisode(after id: String) async throws -> PodcastEpisode? {
        do {
            let data = try await send("GET", "podcasts/episodes/\(id)/next")
            return data.isEmpty ? nil : try decode(PodcastEpisode.self, from: data)
        } catch FlexarrClientError.http(status: 404, _) {
            return nil
        }
    }

    // MARK: Up Next

    /// The episodes to hear after this one, in order, the same on every device. Played ones drop out, and starting one
    /// takes it off. Servers before Up Next have none.
    public func upNext() async throws -> [PodcastEpisode] {
        do {
            return try await get("podcasts/queue")
        } catch FlexarrClientError.http(status: 404, _) {
            return []
        }
    }

    /// Puts an episode in Up Next: first with `next`, else last. One already there moves. Returns the queue.
    public func addToUpNext(_ episodeId: String, next: Bool) async throws -> [PodcastEpisode] {
        let data = try await send("POST", "podcasts/queue", body: UpNextBody(episodeId: episodeId, next: next))
        return try decode([PodcastEpisode].self, from: data)
    }

    /// Up Next in this order; ids left out leave it. Returns the queue.
    public func setUpNext(_ episodeIds: [String]) async throws -> [PodcastEpisode] {
        let data = try await send("PUT", "podcasts/queue", body: UpNextOrderBody(episodeIds: episodeIds))
        return try decode([PodcastEpisode].self, from: data)
    }

    public func removeFromUpNext(_ episodeId: String) async throws {
        _ = try await send("DELETE", "podcasts/queue/\(episodeId)")
    }

    public func podcastPreferences() async throws -> PodcastPreferences {
        try await get("podcasts/preferences")
    }

    public func setPodcastPreferences(_ update: PodcastPreferencesUpdate) async throws -> PodcastPreferences {
        let data = try await send("PUT", "podcasts/preferences", body: update)
        return try decode(PodcastPreferences.self, from: data)
    }

    public func unsubscribe(from podcastId: String) async throws {
        _ = try await send("DELETE", "podcasts/\(podcastId)/subscription")
    }

    /// Fetches the show's feed again now.
    public func refreshPodcast(_ id: String) async throws -> Podcast {
        let data = try await send("POST", "podcasts/\(id)/refresh")
        return try decode(Podcast.self, from: data)
    }

    /// What this phone should have without being asked: the newest unplayed episodes of shows that auto-download.
    public func podcastAutoDownloads() async throws -> [PodcastAutoDownload] {
        try await get("podcasts/auto-downloads")
    }

    /// Episodes this phone has downloaded that have since been played, wherever. Absent from servers before 0.56.
    public func podcastPlayedDownloads() async throws -> [PodcastPlayedDownload] {
        try await get("podcasts/played-downloads")
    }

    /// Apple's directory, searched in the country chosen in Settings.
    public func searchPodcasts(_ query: String) async throws -> [DirectoryPodcast] {
        try await get("podcasts/search", query: [.init(name: "q", value: query)])
    }

    /// The top shows, overall or in one of `podcastGenres()`.
    public func topPodcasts(genre: Int? = nil) async throws -> [DirectoryPodcast] {
        try await get("podcasts/top", query: genre.map { [.init(name: "genre", value: String($0))] } ?? [])
    }

    public func podcastGenres() async throws -> [PodcastGenre] {
        try await get("podcasts/genres")
    }
}
