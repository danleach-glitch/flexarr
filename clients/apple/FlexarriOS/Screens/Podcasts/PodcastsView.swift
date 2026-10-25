import FlexarrKit
import SwiftUI

/// Podcasts: what's part-heard, the newest episodes of the shows followed, and the shows themselves, with Browse
/// for finding more. Without a Podcasts library on the server, it says how to get one.
struct PodcastsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var home = PodcastsHomeModel()

    private var columns: [GridItem] {
        let minimum: CGFloat = sizeClass == .regular ? 150 : 104
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.45), spacing: 14, alignment: .top)]
    }

    var body: some View {
        Group {
            if let status = model.podcastStatus, !status.available {
                StatusView(state: .empty(status.needsLibrary
                    ? "There’s no Podcasts library yet. The main profile can add one in Settings › Libraries on the web, with a folder for episodes on their way to phones."
                    : "Podcasts aren’t available on this profile. The main profile can change which libraries it sees on the web."))
            } else if let error = home.error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else if home.loadedAt == nil {
                StatusView(state: .loading)
            } else {
                content
            }
        }
        .background(RoomBackground(ambient: home.podcasts.first.map { podcastGlow($0.accentColor) }, intensity: 0.18))
        .navigationTitle("Podcasts")
        .toolbar {
            if model.podcastStatus?.available != false {
                NavigationLink { PodcastBrowseView() } label: { Label("Browse", systemImage: "magnifyingglass") }
            }
        }
        .task(id: model.refreshToken) { await load() }
        .onChange(of: model.music.current?.id) { _, _ in Task { await load() } }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, Freshness.isStale(home.loadedAt) { Task { await load() } }
        }
    }

    private var content: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 28) {
                if home.isEmpty {
                    VStack(spacing: 16) {
                        Text("Follow a show and its new episodes come here, and to this \(ThisDevice.name) on Wi-Fi.")
                            .font(.body.weight(.medium))
                            .foregroundStyle(Color.usher)
                            .multilineTextAlignment(.center)
                        NavigationLink { PodcastBrowseView() } label: {
                            Label("Browse podcasts", systemImage: "magnifyingglass")
                        }
                        .buttonStyle(ActionButtonStyle(prominent: true))
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.horizontal, Layout.edge)
                    .padding(.top, 60)
                }
                if !home.listening.isEmpty {
                    episodes("Continue listening", home.listening)
                }
                let unplayed = home.newEpisodes.filter { !$0.isPlayed && !home.listening.map(\.id).contains($0.id) }
                if !unplayed.isEmpty {
                    episodes("New episodes", Array(unplayed.prefix(12)))
                }
                if !home.podcasts.isEmpty {
                    VStack(alignment: .leading, spacing: 12) {
                        SectionHeading(title: "Your podcasts")
                        LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                            ForEach(home.podcasts) { PodcastCard(podcast: $0) }
                        }
                        .padding(.horizontal, Layout.edge)
                    }
                }
            }
            .padding(.top, 8)
            .padding(.bottom, 24)
        }
        .refreshable { await load() }
    }

    private func episodes(_ title: String, _ list: [PodcastEpisode]) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            SectionHeading(title: title)
            VStack(spacing: 0) {
                ForEach(list) { episode in
                    PodcastEpisodeRow(episode: episode, showsPodcast: true)
                    if episode.id != list.last?.id { Divider().overlay(Color.aisle) }
                }
            }
            .padding(.horizontal, Layout.edge)
            .frame(maxWidth: 760, alignment: .leading)
        }
    }

    private func load() async {
        if model.podcastStatus == nil { await model.loadLibraries() }
        guard model.podcastStatus?.available != false else { return }
        await home.load(model: model)
    }
}

/// Finding shows: Apple's charts for the country chosen in Settings on the web, by category, and search. A feed's own
/// address, pasted in, adds that feed (for shows Apple doesn't list).
struct PodcastBrowseView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var browse = PodcastBrowseModel()
    @State private var text = ""

    private var columns: [GridItem] {
        let minimum: CGFloat = sizeClass == .regular ? 150 : 104
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.45), spacing: 14, alignment: .top)]
    }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 16) {
                if let feed = PodcastBrowseModel.feedAddress(text) {
                    Button {
                        Task { await open(await add(feed: feed)) }
                    } label: {
                        if browse.addingFeed {
                            ProgressView().tint(Color.room)
                        } else {
                            Label("Add this feed", systemImage: "plus")
                        }
                    }
                    .buttonStyle(ActionButtonStyle(prominent: true))
                    .padding(.horizontal, Layout.edge)
                } else if let results = browse.results {
                    if results.isEmpty {
                        StatusView(state: .empty("No shows found for “\(text)”."))
                            .frame(height: 240)
                    } else {
                        grid(results)
                    }
                } else {
                    genreChips
                    if browse.top.isEmpty, browse.isLoading {
                        ProgressView().frame(maxWidth: .infinity).padding(40)
                    } else {
                        grid(browse.top)
                    }
                }
            }
            .padding(.top, 8)
            .padding(.bottom, 24)
        }
        .background(RoomBackground(ambient: nil))
        .navigationTitle("Browse")
        .searchable(text: $text, placement: .navigationBarDrawer(displayMode: .always), prompt: "Shows, or a feed’s address")
        .textInputAutocapitalization(.never)
        .task { if browse.top.isEmpty, let client = model.client { await browse.loadTop(client: client) } }
        .task(id: text) { if let client = model.client { await browse.search(text, client: client) } }
        .alert("Couldn't add this", isPresented: Binding(get: { browse.error != nil }, set: { if !$0 { browse.clearError() } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(browse.error ?? "")
        }
    }

    private var genreChips: some View {
        ScrollView(.horizontal) {
            HStack(spacing: 8) {
                chip("Top shows", nil)
                ForEach(browse.genres) { chip($0.name, $0.id) }
            }
            .padding(.horizontal, Layout.edge)
        }
        .scrollIndicators(.hidden)
    }

    private func chip(_ name: String, _ id: Int?) -> some View {
        Button(name) {
            browse.genre = id
            Task { if let client = model.client { await browse.loadTop(client: client) } }
        }
        .buttonStyle(PillButtonStyle(selected: browse.genre == id))
    }

    private func grid(_ shows: [DirectoryPodcast]) -> some View {
        LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
            ForEach(shows) { show in
                DirectoryCard(show: show, isAdding: browse.adding == show.iTunesId) {
                    Task { await open(await add(show)) }
                }
            }
        }
        .padding(.horizontal, Layout.edge)
    }

    private func add(_ show: DirectoryPodcast) async -> Podcast? {
        guard let client = model.client else { return nil }
        return await browse.add(show, client: client)
    }

    private func add(feed: String) async -> Podcast? {
        guard let client = model.client else { return nil }
        return await browse.add(feed: feed, client: client)
    }

    private func open(_ podcast: Podcast?) async {
        if let podcast { router.push(.item(podcast.summary)) }
    }
}
