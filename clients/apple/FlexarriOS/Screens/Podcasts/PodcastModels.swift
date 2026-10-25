import FlexarrKit
import Foundation
import Observation

/// What Podcasts opens to: the episodes part-heard, the newest episodes of the shows followed, and the shows.
@MainActor
@Observable
final class PodcastsHomeModel {
    private(set) var listening: [PodcastEpisode] = []
    private(set) var newEpisodes: [PodcastEpisode] = []
    private(set) var podcasts: [Podcast] = []
    private(set) var error: String?
    private(set) var isLoading = false
    private(set) var loadedAt: Date?

    var isEmpty: Bool { podcasts.isEmpty && listening.isEmpty }

    func load(model: AppModel) async {
        guard let client = model.client else { return }
        isLoading = loadedAt == nil
        defer { isLoading = false }
        do {
            async let continuing = client.continueListening()
            async let fresh = client.newPodcastEpisodes(unplayed: true, take: 30)
            podcasts = try await client.podcasts()
            listening = (try? await continuing) ?? []
            newEpisodes = (try? await fresh) ?? []
            error = nil
            loadedAt = Date()
        } catch {
            if loadedAt == nil { self.error = ConnectionError(error).message }
        }
    }
}

/// A show's page: the show, whether it's followed, and its episodes, newest first: the unplayed ones, or all.
@MainActor
@Observable
final class PodcastPageModel {
    let id: String
    private(set) var podcast: Podcast?
    private(set) var episodes: [PodcastEpisode] = []
    private(set) var total = 0
    private(set) var error: String?
    private(set) var isLoadingMore = false
    /// Following or unfollowing, or saving a setting.
    private(set) var isSaving = false
    var unplayedOnly = true
    @ObservationIgnored private var generation = 0

    private static let pageSize = 40

    init(id: String) {
        self.id = id
    }

    func load(model: AppModel) async {
        guard let client = model.client else { return }
        generation += 1
        let mine = generation
        do {
            async let page = client.podcastEpisodes(id, unplayed: unplayedOnly, take: Self.pageSize)
            let loaded = try await client.podcast(id)
            let first = try await page
            guard mine == generation else { return }
            podcast = loaded
            episodes = first.items
            total = first.total
            error = nil
        } catch {
            if podcast == nil, mine == generation { self.error = ConnectionError(error).message }
        }
    }

    func loadMoreIfNeeded(at index: Int, client: FlexarrClient) {
        guard !isLoadingMore, episodes.count < total, index >= episodes.count - 10 else { return }
        let mine = generation
        let skip = episodes.count
        isLoadingMore = true
        Task {
            defer { isLoadingMore = false }
            if let page = try? await client.podcastEpisodes(id, unplayed: unplayedOnly, skip: skip, take: Self.pageSize),
               mine == generation, skip == episodes.count {
                let known = Set(episodes.map(\.id))
                episodes.append(contentsOf: page.items.filter { !known.contains($0.id) })
                total = page.total
            }
        }
    }

    func follow(_ following: Bool, model: AppModel) async {
        guard let client = model.client else { return }
        isSaving = true
        defer { isSaving = false }
        do {
            if following {
                podcast = try await client.subscribe(to: id)
            } else {
                try await client.unsubscribe(from: id)
                podcast = try await client.podcast(id)
            }
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    /// Every episode played: for catching up on a show by starting from now.
    func markAllPlayed(model: AppModel) async {
        guard let podcast else { return }
        await model.setWatched(podcast.summary, true)
        await load(model: model)
    }
}

/// Finding shows: Apple's top charts (overall or by category) and search. A feed's address typed in adds that feed.
@MainActor
@Observable
final class PodcastBrowseModel {
    private(set) var genres: [PodcastGenre] = []
    var genre: Int?
    private(set) var top: [DirectoryPodcast] = []
    private(set) var results: [DirectoryPodcast]?
    private(set) var error: String?
    private(set) var isLoading = false
    /// A show being added to the library (by Apple's id), to show which one's spinning.
    private(set) var adding: Int64?
    private(set) var addingFeed = false

    /// An http or https address: the box holds a feed, not words to search for.
    static func feedAddress(_ text: String) -> String? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: trimmed), let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https", url.host != nil else { return nil }
        return trimmed
    }

    func loadTop(client: FlexarrClient) async {
        isLoading = true
        defer { isLoading = false }
        if genres.isEmpty { genres = (try? await client.podcastGenres()) ?? [] }
        let wanted = genre
        do {
            let found = try await client.topPodcasts(genre: wanted)
            guard wanted == genre else { return }
            top = found
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    func search(_ text: String, client: FlexarrClient) async {
        let words = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !words.isEmpty, Self.feedAddress(words) == nil else {
            results = nil
            return
        }
        // Typing: wait for a pause before asking Apple.
        try? await Task.sleep(for: .milliseconds(350))
        guard !Task.isCancelled else { return }
        do {
            results = try await client.searchPodcasts(words)
            error = nil
        } catch {
            if !Task.isCancelled { self.error = ConnectionError(error).message }
        }
    }

    /// Puts a show from the directory in the library (or finds it there), for its page to open.
    func add(_ show: DirectoryPodcast, client: FlexarrClient) async -> Podcast? {
        adding = show.iTunesId
        defer { adding = nil }
        do {
            return try await client.addPodcast(iTunesId: show.iTunesId)
        } catch {
            self.error = ConnectionError(error).message
            return nil
        }
    }

    func add(feed: String, client: FlexarrClient) async -> Podcast? {
        addingFeed = true
        defer { addingFeed = false }
        do {
            return try await client.addPodcast(feedUrl: feed)
        } catch {
            self.error = ConnectionError(error).message
            return nil
        }
    }

    func clearError() {
        error = nil
    }
}

extension Podcast {
    /// "3 unplayed", "Up to date", "1,204 episodes": what a show's card says under its name.
    var countLine: String {
        if isSubscribed { return unplayedCount == 0 ? "Up to date" : "\(unplayedCount.formatted()) unplayed" }
        return episodeCount == 1 ? "1 episode" : "\(episodeCount.formatted()) episodes"
    }
}

extension PodcastEpisode {
    /// "Today", "Yesterday", "Mon", "21 Oct", "21 Oct 2024".
    var dateLine: String? {
        guard let publishedAt else { return nil }
        let calendar = Calendar.current
        if calendar.isDateInToday(publishedAt) { return "Today" }
        if calendar.isDateInYesterday(publishedAt) { return "Yesterday" }
        if let days = calendar.dateComponents([.day], from: publishedAt, to: Date()).day, days < 7 {
            return publishedAt.formatted(.dateTime.weekday(.wide))
        }
        if calendar.isDate(publishedAt, equalTo: Date(), toGranularity: .year) {
            return publishedAt.formatted(.dateTime.day().month(.abbreviated))
        }
        return publishedAt.formatted(.dateTime.day().month(.abbreviated).year())
    }

    /// "1h 6m", or "24m left" part way through, or "Played".
    var timeLine: String? {
        if isPlayed { return "Played" }
        if let duration = durationSeconds, userData.positionSeconds > 0 {
            return Format.remaining(duration - userData.positionSeconds)
        }
        return durationSeconds.flatMap(Format.runtime(seconds:))
    }
}

/// "1×", "1.5×", "1.75×".
func speedLabel(_ speed: Double) -> String {
    speed.formatted(.number.precision(.fractionLength(0...2))) + "×"
}
