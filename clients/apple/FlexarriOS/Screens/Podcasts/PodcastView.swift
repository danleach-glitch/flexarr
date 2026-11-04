import FlexarrKit
import SwiftUI

/// A show: its artwork, who makes it and what it's about, Follow and Play, then its episodes: the unplayed ones or all,
/// newest or oldest first (chosen in the ⋯ menu, which also marks them all played).
struct PodcastView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @Environment(\.scenePhase) private var scenePhase
    /// Only the screen on show refreshes when the app comes back, not every one left open in a tab or below it.
    @State private var isVisible = false
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var page: PodcastPageModel
    @State private var markingAllPlayed = false
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
                // A List, for the episodes' swipe actions; it draws nothing of its own.
                List {
                    header(podcast)
                        .padding(.bottom, 8)
                        .bareListRow()
                    episodesHeading(podcast)
                        .episodeListRow()
                        .listRowSeparator(.hidden)
                    if page.episodes.isEmpty {
                        StatusView(state: .empty(page.unplayedOnly ? t("You’re up to date.") : t("No episodes yet.")))
                            .frame(height: 160)
                            .bareListRow()
                    }
                    ForEach(Array(page.episodes.enumerated()), id: \.element.id) { index, episode in
                        PodcastEpisodeRow(episode: episode) { played in
                            Task {
                                await page.setPlayed(episode, played, model: model)
                                await downloads.reconcilePodcasts()
                            }
                        }
                            .onAppear { if let client = model.client { page.loadMoreIfNeeded(at: index, client: client) } }
                            .episodeListRow()
                    }
                    if page.isLoadingMore {
                        ProgressView().frame(maxWidth: .infinity).padding(20).bareListRow()
                    }
                    Color.clear.frame(height: 24).bareListRow()
                }
                .listStyle(.plain)
                .scrollContentBackground(.hidden)
                .refreshable { await refresh() }
            } else if let error = page.error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(.hidden, for: .navigationBar)
        .alert(t("Mark all as played?"), isPresented: $markingAllPlayed) {
            Button(t("Mark all as played")) {
                Task {
                    await page.markAllPlayed(model: model)
                    await downloads.reconcilePodcasts()
                }
            }
            Button(t(Words.cancel), role: .cancel) {}
        } message: {
            if let podcast = page.podcast {
                Text(t("All {count} episodes of {podcast} will be marked as played.", ["count": podcast.episodeCount, "podcast": podcast.title]))
            }
        }
        .alert(t("Stop following this show?"), isPresented: $unfollowing) {
            Button(t(Words.stopFollowing), role: .destructive) {
                Task {
                    await page.follow(false, model: model)
                    await downloads.reconcilePodcasts()
                }
            }
            Button(t(Words.cancel), role: .cancel) {}
        } message: {
            Text(t("Its new episodes stop coming to this {device}. Episodes already downloaded stay until you delete them.", ["device": ThisDevice.name]))
        }
        #if !os(macOS)
        .toolbar {
            if let podcast = page.podcast { moreMenu(podcast) }
        }
        #endif
        .task(id: model.refreshToken) {
            if page.skipsNextReload {
                page.skipsNextReload = false
                return
            }
            await load()
        }
        .onChange(of: page.unplayedOnly) { _, _ in Task { await load() } }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible { Task { await load() } }
        }
    }

    private func header(_ podcast: Podcast) -> some View {
        VStack(spacing: 16) {
            PodcastArtwork(path: podcast.artwork.poster, title: podcast.title, accent: podcast.accentColor, imageWidth: 780, cornerRadius: Radius.art)
                .frame(width: sizeClass == .regular ? 300 : 220)
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
                FactsLine(facts: [podcast.categories.first, podcast.countLine, podcast.explicit ? t("Explicit") : nil].compactMap { $0 })
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
                        Label(podcast.isSubscribed ? t(Words.following) : t(Words.follow), systemImage: podcast.isSubscribed ? "checkmark" : "plus")
                    }
                }
                .buttonStyle(ActionButtonStyle(ambient: podcastGlow(podcast.accentColor), prominent: !podcast.isSubscribed))
                if let next = page.nextToPlay {
                    Button { model.playEpisode(next.summary) } label: {
                        Label(next.progress != nil ? t("Resume") : page.oldestFirst ? t(Words.play) : t("Latest episode"), systemImage: Symbol.play)
                    }
                    .buttonStyle(ActionButtonStyle(ambient: podcastGlow(podcast.accentColor), prominent: podcast.isSubscribed))
                }
            }
            .fixedSize()
            if let error = podcast.error {
                Text(t("The feed couldn’t be checked last time: {error}", ["error": error]))
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

    /// The order of the episodes, and Mark all as played: in the navigation bar on the iPhone, beside Unplayed and All
    /// on the Mac (whose toolbar holds only back and the page's switch).
    private func moreMenu(_ podcast: Podcast) -> some View {
        Menu {
            Section(t("Order")) {
                order(t("Newest first"), oldest: false)
                order(t("Oldest first"), oldest: true)
            }
            Button { markingAllPlayed = true } label: { Label(t("Mark all as played"), systemImage: Symbol.watched) }
                .disabled(podcast.unplayedCount == 0)
        } label: {
            Label(t("More"), systemImage: Symbol.more)
        }
    }

    /// How many episodes, and Unplayed or All.
    private func episodesHeading(_ podcast: Podcast) -> some View {
        HStack(spacing: 12) {
            Text(page.unplayedOnly ? t("{count} unplayed", ["count": page.total]) : t("{count, plural, one {# episode} other {# episodes}}", ["count": page.total]))
                .font(.marquee(22, weight: .bold))
                .foregroundStyle(Color.screen)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
            Spacer(minLength: 0)
            @Bindable var page = page
            Picker(t("Show"), selection: $page.unplayedOnly) {
                Text(t("Unplayed")).tag(true)
                Text(t("All")).tag(false)
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .fixedSize()
            #if os(macOS)
            moreMenu(podcast)
                .labelStyle(.iconOnly)
                .menuIndicator(.hidden)
                .fixedSize()
            #endif
        }
        .padding(.top, 12)
    }

    /// An order in the menu, ticked when it's the one showing.
    private func order(_ title: String, oldest: Bool) -> some View {
        Button {
            Task { await page.setOldestFirst(oldest, model: model) }
        } label: {
            if page.oldestFirst == oldest {
                Label(title, systemImage: "checkmark")
            } else {
                Text(title)
            }
        }
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
