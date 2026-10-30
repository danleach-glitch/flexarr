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

/// A show's page: the show, whether it's followed, and its episodes, the unplayed ones or all, newest or oldest first.
/// A followed show's order is kept on the server, so every device lists it the same way; marking an episode played
/// shows at once, and the server is told after.
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
    /// Oldest first: from a followed show's subscription, else what was chosen on this screen.
    private(set) var oldestFirst = false
    /// The page changed something itself and has shown it already: the reload everyone else gets isn't needed here.
    @ObservationIgnored var skipsNextReload = false
    @ObservationIgnored private var orderKnown = false
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
            // The order comes with the show the first time, so its episodes wait for it.
            if !orderKnown {
                let show = try await client.podcast(id)
                oldestFirst = show.subscription?.oldestFirst ?? false
                orderKnown = true
            }
            async let page = client.podcastEpisodes(id, sort: sort, unplayed: unplayedOnly, take: Self.pageSize)
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
            if let page = try? await client.podcastEpisodes(id, sort: sort, unplayed: unplayedOnly, skip: skip, take: Self.pageSize),
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
                // The order chosen while looking carries over to the subscription.
                podcast = try await client.subscribe(to: id, oldestFirst: oldestFirst ? true : nil)
            } else {
                try await client.unsubscribe(from: id)
                podcast = try await client.podcast(id)
            }
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    private var sort: PodcastEpisodeSort { oldestFirst ? .oldest : .newest }

    /// What Play starts: an episode part-heard, else the first unplayed one in the order shown (newest or oldest).
    var nextToPlay: PodcastEpisode? {
        episodes.first { $0.progress != nil } ?? episodes.first { !$0.isPlayed }
    }

    func setOldestFirst(_ oldest: Bool, model: AppModel) async {
        guard oldest != oldestFirst, let client = model.client else { return }
        oldestFirst = oldest
        orderKnown = true
        if podcast?.isSubscribed == true, let saved = try? await client.subscribe(to: id, oldestFirst: oldest) {
            podcast = saved
        }
        await load(model: model)
    }

    /// Marks an episode played or unplayed here at once (on Unplayed, a played one goes), then on the server.
    /// Unplayed part way through forgets the place reached.
    func setPlayed(_ episode: PodcastEpisode, _ played: Bool, model: AppModel) async {
        let changes = episode.isPlayed != played
        guard let client = model.client, changes || (!played && episode.progress != nil) else { return }
        if unplayedOnly, played {
            episodes.removeAll { $0.id == episode.id }
            total = max(0, total - 1)
        } else if let index = episodes.firstIndex(where: { $0.id == episode.id }) {
            episodes[index] = episode.with(played: played)
        }
        if let podcast, changes { self.podcast = podcast.with(unplayedCount: podcast.unplayedCount + (played ? -1 : 1)) }
        do {
            try await client.setWatched(episode.id, watched: played)
        } catch {
            await load(model: model) // didn't stick: back to what the server has
            return
        }
        if played { model.player.offlineLibrary?.markedPlayed(itemId: episode.id) } else { model.player.offlineLibrary?.forgetPosition(itemId: episode.id) }
        // Other screens catch up; this one already shows it, and only its counts are checked.
        skipsNextReload = true
        model.invalidate()
        if let fresh = try? await client.podcast(id) { podcast = fresh }
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
