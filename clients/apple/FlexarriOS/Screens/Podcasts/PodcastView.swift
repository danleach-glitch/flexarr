import FlexarrKit
import SwiftUI

/// A show: its artwork, who makes it and what it's about, Follow (and how it's followed), then its episodes newest
/// first, or only the unplayed ones.
struct PodcastView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var page: PodcastPageModel
    @State private var showingSettings = false
    @State private var expanded = false
    @State private var unfollowing = false
    /// What the page showed before it loaded (the card that was tapped).
    private let preview: ItemSummary?

    init(id: String, preview: ItemSummary? = nil) {
        _page = State(initialValue: PodcastPageModel(id: id))
        self.preview = preview
    }

    var body: some View {
        ZStack {
            RoomBackground(ambient: podcastGlow(page.podcast?.accentColor ?? preview?.accentColor), intensity: 0.3)
            if let podcast = page.podcast {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 20) {
                        header(podcast)
                        episodes
                    }
                    .padding(.bottom, 32)
                }
                .refreshable { await refresh() }
            } else if let error = page.error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(.hidden, for: .navigationBar)
        .toolbar {
            if page.podcast?.isSubscribed == true {
                Button { showingSettings = true } label: { Label("Settings for this show", systemImage: "slider.horizontal.3") }
            }
        }
        .sheet(isPresented: $showingSettings) {
            if let podcast = page.podcast, let subscription = podcast.subscription {
                PodcastSettingsSheet(podcast: podcast, subscription: subscription) { change in
                    Task {
                        await page.update(change, model: model)
                        await downloads.reconcilePodcasts()
                    }
                }
                .presentationDetents([.medium])
                .presentationBackground(Color.room)
            }
        }
        .confirmationDialog("Stop following this show?", isPresented: $unfollowing, titleVisibility: .visible) {
            Button("Stop following", role: .destructive) {
                Task {
                    await page.follow(false, model: model)
                    await downloads.reconcilePodcasts()
                }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Its new episodes stop coming to this \(ThisDevice.name). Episodes already downloaded stay until you delete them.")
        }
        .task(id: model.refreshToken) { await load() }
        .onChange(of: page.unplayedOnly) { _, _ in Task { await load() } }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active { Task { await load() } }
        }
    }

    private func header(_ podcast: Podcast) -> some View {
        VStack(spacing: 16) {
            PodcastArtwork(path: podcast.artwork.poster, title: podcast.title, accent: podcast.accentColor, imageWidth: 780, cornerRadius: 12)
                .frame(width: sizeClass == .regular ? 300 : 220)
                .shadow(color: .black.opacity(0.45), radius: 20, y: 10)
            VStack(spacing: 6) {
                Text(podcast.title)
                    .font(.marquee(sizeClass == .regular ? 38 : 28, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                    .lineLimit(3)
                    .minimumScaleFactor(0.7)
                if let author = podcast.author {
                    Text(author)
                        .font(.body.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                        .multilineTextAlignment(.center)
                }
                FactsLine(facts: [podcast.categories.first, podcast.countLine, podcast.explicit ? "Explicit" : nil].compactMap { $0 })
            }
            HStack(spacing: 12) {
                Button {
                    if podcast.isSubscribed {
                        unfollowing = true
                    } else {
                        Task {
                            await page.follow(true, model: model)
                            await downloads.reconcilePodcasts()
                        }
                    }
                } label: {
                    if page.isSaving {
                        ProgressView().tint(podcast.isSubscribed ? Color.screen : Color.room)
                    } else {
                        Label(podcast.isSubscribed ? "Following" : "Follow", systemImage: podcast.isSubscribed ? "checkmark" : "plus")
                    }
                }
                .buttonStyle(ActionButtonStyle(ambient: podcastGlow(podcast.accentColor), prominent: !podcast.isSubscribed))
                if let latest = page.episodes.first(where: { !$0.isPlayed }) {
                    Button { model.playEpisode(latest.summary, speed: podcast.subscription?.playbackSpeed) } label: {
                        Label(latest.progress != nil ? "Resume" : "Latest episode", systemImage: "play.fill")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: podcastGlow(podcast.accentColor), prominent: podcast.isSubscribed))
                }
            }
            .fixedSize()
            if let error = podcast.error {
                Text("The feed couldn’t be checked last time: \(error)")
                    .font(.caption)
                    .foregroundStyle(Color.danger)
                    .multilineTextAlignment(.center)
            }
            if let overview = podcast.overview {
                Button { withAnimation { expanded.toggle() } } label: {
                    Text(overview)
                        .font(.subheadline)
                        .foregroundStyle(Color.screen.opacity(0.85))
                        .lineLimit(expanded ? nil : 3)
                        .multilineTextAlignment(.leading)
                        .frame(maxWidth: 640, alignment: .leading)
                }
                .buttonStyle(.plain)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 12)
        .padding(.horizontal, Layout.edge)
    }

    private var episodes: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(page.unplayedOnly ? "\(page.total.formatted()) unplayed" : page.total == 1 ? "1 episode" : "\(page.total.formatted()) episodes")
                    .font(.marquee(22, weight: .bold))
                    .foregroundStyle(Color.screen)
                Spacer()
                @Bindable var page = page
                Picker("Show", selection: $page.unplayedOnly) {
                    Text("All").tag(false)
                    Text("Unplayed").tag(true)
                }
                .pickerStyle(.segmented)
                .fixedSize()
            }
            if page.episodes.isEmpty {
                StatusView(state: .empty(page.unplayedOnly ? "You’re up to date." : "No episodes yet."))
                    .frame(height: 160)
            }
            LazyVStack(spacing: 0) {
                ForEach(Array(page.episodes.enumerated()), id: \.element.id) { index, episode in
                    PodcastEpisodeRow(episode: episode)
                        .onAppear { if let client = model.client { page.loadMoreIfNeeded(at: index, client: client) } }
                    Divider().overlay(Color.aisle)
                }
            }
            if page.isLoadingMore {
                ProgressView().frame(maxWidth: .infinity).padding(20)
            }
        }
        .padding(.horizontal, Layout.edge)
        .frame(maxWidth: 760)
        .frame(maxWidth: .infinity)
    }

    private func load() async {
        await page.load(model: model)
    }

    /// Pulled down: the feed is fetched again first, for an episode out a minute ago.
    private func refresh() async {
        if let client = model.client { _ = try? await client.refreshPodcast(page.id) }
        await load()
    }
}

/// How a followed show behaves on this profile: whether phones keep its newest episodes, how many, and its speed.
struct PodcastSettingsSheet: View {
    @Environment(DownloadStore.self) private var downloads
    let podcast: Podcast
    let subscription: PodcastSubscription
    let save: (PodcastSubscriptionUpdate) -> Void
    @State private var autoDownload: Bool
    @State private var keep: Int
    /// 0 for the usual speed.
    @State private var speed: Double

    init(podcast: Podcast, subscription: PodcastSubscription, save: @escaping (PodcastSubscriptionUpdate) -> Void) {
        self.podcast = podcast
        self.subscription = subscription
        self.save = save
        _autoDownload = State(initialValue: subscription.autoDownload)
        _keep = State(initialValue: subscription.keepEpisodes)
        _speed = State(initialValue: subscription.playbackSpeed ?? 0)
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Toggle("Download new episodes", isOn: $autoDownload)
                        .tint(.silver)
                        .listRowBackground(Color.seat)
                    Stepper("Keep the newest \(keep)", value: $keep, in: 1...20)
                        .disabled(!autoDownload)
                        .listRowBackground(Color.seat)
                } footer: {
                    Text(downloads.autoDownloadsPodcasts
                         ? "On Wi-Fi only. Played episodes are deleted as newer ones arrive."
                         : "Downloading new episodes is turned off on this \(ThisDevice.name), in Settings.")
                }
                Section {
                    Picker("Speed", selection: $speed) {
                        Text("Usual").tag(0.0)
                        ForEach(MusicPlayer.speeds, id: \.self) { Text(speedLabel($0)).tag($0) }
                    }
                    .listRowBackground(Color.seat)
                } footer: {
                    Text("Usual is the speed you last chose in the player.")
                }
            }
            .scrollContentBackground(.hidden)
            .navigationTitle(podcast.title)
            .navigationBarTitleDisplayMode(.inline)
        }
        .onChange(of: autoDownload) { _, value in save(.init(autoDownload: value)) }
        .onChange(of: keep) { _, value in save(.init(keepEpisodes: value)) }
        .onChange(of: speed) { _, value in save(.init(playbackSpeed: value)) }
    }
}
