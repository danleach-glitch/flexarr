import FlexarrKit
import SwiftUI

/// Podcasts: the shows followed in a row, then what's part-heard and the newest unplayed episodes, with Browse for
/// finding more. A profile the Podcasts library is hidden from is told so.
struct PodcastsView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var home = PodcastsHomeModel()
    @State private var editingUpNext = false

    var body: some View {
        Group {
            if let status = model.podcastStatus, !status.available {
                StatusView(state: .empty("Podcasts aren’t available on this profile."))
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
                Button { router.push(.podcastBrowse) } label: { Label("Browse", systemImage: "magnifyingglass") }
            }
        }
        .task(id: model.refreshToken) { await load() }
        .onChange(of: model.music.current?.id) { _, _ in Task { await load() } }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, Freshness.isStale(home.loadedAt) { Task { await load() } }
        }
    }

    /// A List, for the episodes' swipe actions; it draws nothing of its own.
    private var content: some View {
        List {
            if home.isEmpty {
                VStack(spacing: 16) {
                    Text("Follow a show and its new episodes come here, and to this \(ThisDevice.name) on Wi-Fi.")
                        .font(.body.weight(.medium))
                        .foregroundStyle(Color.usher)
                        .multilineTextAlignment(.center)
                    Button { router.push(.podcastBrowse) } label: {
                        Label("Browse podcasts", systemImage: "magnifyingglass")
                    }
                    .buttonStyle(ActionButtonStyle(prominent: true))
                }
                .frame(maxWidth: .infinity)
                .padding(.horizontal, Layout.edge)
                .padding(.top, 60)
                .bareListRow()
            }
            if !home.podcasts.isEmpty {
                VStack(alignment: .leading, spacing: 10) {
                    SectionHeading(title: "Your podcasts")
                    ScrollView(.horizontal) {
                        LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                            // Artwork alone, the most recently updated first (as the server lists them).
                            ForEach(home.podcasts) { PodcastCard(podcast: $0, showsTitle: false).frame(width: Layout.posterWidth(sizeClass)) }
                        }
                        .padding(.horizontal, Layout.edge)
                    }
                    .scrollIndicators(.hidden)
                }
                .padding(.vertical, 8)
                .bareListRow()
            }
            if !model.upNext.isEmpty {
                upNextSection
            }
            if !home.listening.isEmpty {
                episodes("Continue listening", home.listening)
            }
            let listening = Set(home.listening.map(\.id))
            let unplayed = home.newEpisodes.filter { !$0.isPlayed && !listening.contains($0.id) }
            if !unplayed.isEmpty {
                episodes("New episodes", Array(unplayed.prefix(15)), compact: true)
            }
            Color.clear.frame(height: 24).bareListRow()
        }
        .listStyle(.plain)
        .scrollContentBackground(.hidden)
        .environment(\.editMode, .constant(editingUpNext ? .active : .inactive))
        .refreshable { await load() }
    }

    /// Up Next, the same on every device: drag into a new order (Edit), swipe one off.
    @ViewBuilder
    private var upNextSection: some View {
        HStack(alignment: .firstTextBaseline) {
            Text("Up Next")
                .font(.marquee(26, weight: .bold))
                .foregroundStyle(Color.screen)
            Spacer()
            if model.upNext.count > 1 {
                Button(editingUpNext ? "Done" : "Edit") { withAnimation { editingUpNext.toggle() } }
                    .buttonStyle(.borderless)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.usher)
            }
        }
        .padding(.horizontal, Layout.edge)
        .padding(.top, 20)
        .bareListRow()
        ForEach(model.upNext) { episode in
            PodcastEpisodeRow(episode: episode, showsPodcast: true, compact: true, removeFromUpNext: {
                Task { await model.removeFromUpNext(episode.id) }
            })
            .episodeListRow()
        }
        .onMove { from, to in
            var order = model.upNext
            order.move(fromOffsets: from, toOffset: to)
            Task { await model.reorderUpNext(order) }
        }
    }

    @ViewBuilder
    private func episodes(_ title: String, _ list: [PodcastEpisode], compact: Bool = false) -> some View {
        SectionHeading(title: title)
            .padding(.top, 20)
            .bareListRow()
        ForEach(list) { episode in
            PodcastEpisodeRow(episode: episode, showsPodcast: true, compact: compact)
                .episodeListRow()
        }
    }

    private func load() async {
        if model.podcastStatus == nil { await model.loadLibraries() }
        guard model.podcastStatus?.available != false else { return }
        async let queue: Void = model.loadUpNext()
        await home.load(model: model)
        await queue
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
