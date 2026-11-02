import FlexarrKit
import SwiftUI

/// An artist: their picture and name, their buttons (`MusicActions`: Play, Shuffle, Radio, Follow), and their albums
/// newest first. A followed artist with no albums yet (a placeholder) has only Follow, and what's outside the library.
struct ArtistView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @State private var page: MusicPageModel
    @Environment(\.scenePhase) private var scenePhase
    /// Only the screen on show refreshes when the app comes back, not every one left open in a tab or below it.
    @State private var isVisible = false
    @Environment(\.horizontalSizeClass) private var sizeClass
    /// From outside the library: their albums it lacks, artists like them, and whether they're followed. Nil while
    /// music requests are off (and on servers before them).
    @State private var extras: MusicArtistExtras?
    /// Music requests here: nil until asked, and then Follow shows only when they're on (`configured`).
    @State private var status: MusicRequestStatus?
    /// Paused by the screen that opened this one, or Lidarr not answering here.
    @Environment(\.musicRequestsPaused) private var pausedAbove
    @Environment(\.dismiss) private var dismiss

    init(id: String, preview: ItemSummary?) {
        _page = State(initialValue: MusicPageModel(id: id, preview: preview))
    }

    private var ambient: Color { page.item?.glowColor ?? .silver }
    private var columns: [GridItem] {
        let minimum: CGFloat = sizeClass == .regular ? 150 : 105
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.45), spacing: 12, alignment: .top)]
    }

    var body: some View {
        ZStack {
            RoomBackground(ambient: ambient, intensity: 0.3)
            if let item = page.item {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 24) {
                        header(item)
                        if !page.albums.isEmpty {
                            VStack(alignment: .leading, spacing: 12) {
                                SectionHeading(title: "Albums")
                                LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                                    ForEach(page.albums) { album in
                                        AlbumCard(item: album)
                                    }
                                }
                                .padding(.horizontal, Layout.edge)
                            }
                        } else if page.detail != nil, !hasOutside {
                            StatusView(state: .empty("No albums yet.")).frame(height: 160)
                        }
                        if let extras {
                            if !extras.missing.isEmpty {
                                VStack(alignment: .leading, spacing: 12) {
                                    SectionHeading(title: "Not in your library")
                                    LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                                        ForEach(extras.missing.listed) { entry in
                                            ExternalMusicCard(item: entry.item, showsYear: true)
                                        }
                                    }
                                    .padding(.horizontal, Layout.edge)
                                }
                            }
                            if !extras.similar.isEmpty {
                                ExternalMusicRow(title: "Fans also like", items: extras.similar)
                            }
                        }
                    }
                    .padding(.bottom, 32)
                }
                .refreshable { await load() }
                .environment(\.musicRequestsPaused, paused)
            } else if let error = page.error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(.hidden, for: .navigationBar)
        .task(id: model.refreshToken) { await load(ifFresh: true) }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible { Task { await load(ifFresh: true) } }
        }
    }

    /// Lidarr is connected but isn't answering (or didn't for this page, so whether they're followed is unknown): Follow
    /// waits, greyed.
    private var paused: Bool {
        pausedAbove || (status?.configured == true && status?.reachable == false) || extras?.lidarrAnswered == false
    }

    /// Albums of theirs outside the library, or artists like them, to fill the page when it has no albums.
    private var hasOutside: Bool { !(extras?.missing.isEmpty ?? true) || !(extras?.similar.isEmpty ?? true) }

    private var actions: MusicActions {
        // While the albums load, Play and Shuffle stand (greyed) rather than appear late.
        MusicActions(kind: .artist, platform: .phone, hasMusic: page.detail == nil || !page.albums.isEmpty,
                     canAsk: extras != nil && status?.configured == true)
    }

    private func header(_ item: ItemSummary) -> some View {
        VStack(spacing: 16) {
            SquareArtwork(item: item, circular: true)
                .frame(width: sizeClass == .regular ? 240 : 180)
            VStack(spacing: 6) {
                Text(item.title)
                    .font(.marquee(sizeClass == .regular ? 44 : 34, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                    .lineLimit(2)
                    .minimumScaleFactor(0.7)
                FactsLine(facts: item.facts(scores: !model.isKids))
                if let genres = page.detail?.genres, !genres.isEmpty {
                    Text(genres.prefix(3).joined(separator: ", "))
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(Color.usher)
                        .lineLimit(1)
                }
            }
            // Play spelled out, the rest round; on two lines when they don't fit across.
            ActionRowLayout {
                ForEach(actions.buttons, id: \.self) { button in
                    actionButton(button, item: item)
                }
                if page.isQueueing { ProgressView() }
            }
            .padding(.horizontal, Layout.edge)
            if paused, actions.has(.follow) {
                Text(MusicWords.lidarrNotAnswering)
                    .font(.footnote)
                    .foregroundStyle(Color.usher)
            }
            if let overview = page.detail?.overview, !overview.isEmpty {
                Text(overview)
                    .font(.subheadline)
                    .lineSpacing(3)
                    .foregroundStyle(Color.screen.opacity(0.88))
                    .lineLimit(4)
                    .frame(maxWidth: 680, alignment: .leading)
                    .padding(.horizontal, Layout.edge)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 12)
    }

    @ViewBuilder
    private func actionButton(_ button: MusicActions.Button, item: ItemSummary) -> some View {
        let nothingToPlay = page.albums.isEmpty || page.isQueueing
        switch button {
        case .play:
            Button { Task { await model.playAll(of: page, shuffled: false, router: router) } } label: {
                Label("Play", systemImage: Symbol.play)
            }
            .buttonStyle(ActionButtonStyle(ambient: page.item?.ambientColor, prominent: true, singleLine: true))
            .disabled(nothingToPlay)
        case .shuffle:
            Button { Task { await model.playAll(of: page, shuffled: true, router: router) } } label: {
                Label("Shuffle", systemImage: Symbol.shuffle)
            }
            .buttonStyle(RoundActionStyle())
            .disabled(nothingToPlay)
            .accessibilityLabel("Shuffle")
        case .radio:
            Button { Task { await model.playArtist(item, radio: true, router: router) } } label: {
                Label("Radio", systemImage: Symbol.radio)
            }
            .buttonStyle(RoundActionStyle())
            .disabled(nothingToPlay)
            .accessibilityLabel("Radio")
        case .follow:
            if let extras {
                FollowArtistButton(name: item.title, following: model.isFollowing(libraryArtist: page.id, followed: extras.followed),
                                   libraryId: page.id, deezerId: extras.deezerId, ambient: ambient,
                                   labelled: actions.buttons == [.follow]) {
                    // A placeholder (followed, no albums yet) leaves the library when unfollowed: so does its page, and
                    // the library's lists load again without it.
                    if page.albums.isEmpty, page.detail != nil {
                        dismiss()
                        model.invalidate()
                    }
                }
            }
        case .download, .request, .more:
            EmptyView()
        }
    }

    private func load(ifFresh: Bool = false) async {
        guard let client = model.client else { return }
        async let outside = loadExtras(client, fresh: !ifFresh)
        await page.load(client: client, onlyIfStale: ifFresh)
        await outside
    }

    /// Off (kids, no Lidarr, an older server) leaves the page as it was, without Follow. The extras are asked for only
    /// once the status says requests are on (the server refuses them otherwise); the status is usually kept from the
    /// last music page, so this rarely waits on it.
    private func loadExtras(_ client: FlexarrClient, fresh: Bool) async {
        guard !model.isKids else { return }
        let found = await model.musicRequestStatus(fresh: fresh)
        status = found
        guard found.configured else {
            extras = nil
            return
        }
        if let more = try? await client.musicArtistExtras(libraryItemId: page.id) { extras = more }
    }
}
