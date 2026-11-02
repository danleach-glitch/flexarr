import FlexarrKit
import SwiftUI

/// An artist: their picture and name, their buttons (`MusicActions`: Play, Shuffle, Radio, Follow), and their albums
/// newest first. A followed artist with no albums yet (a placeholder) has only Follow, and what's outside the library.
struct ArtistView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @State private var page: MusicPageModel
    @Environment(\.scenePhase) private var scenePhase
    /// From outside the library: their albums it lacks, artists like them, and whether they're followed. Nil while
    /// music requests are off (and on servers before them).
    @State private var extras: MusicArtistExtras?
    /// Music requests here: nil until asked, and then Follow shows only when they're on (`configured`).
    @State private var status: MusicRequestStatus?
    @Environment(\.musicRequestsPaused) private var pausedAbove
    @Environment(\.dismiss) private var dismiss
    /// The focused round button, named under the row.
    @FocusState private var focusedAction: MusicActions.Button?

    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    init(id: String, preview: ItemSummary?) {
        _page = State(initialValue: MusicPageModel(id: id, preview: preview))
    }

    private var ambient: Color { page.item?.glowColor ?? .silver }

    var body: some View {
        ZStack {
            RoomBackground(ambient: ambient, intensity: 0.4)
                // The whole screen, as a title's backdrop is: left to fill the space it's offered, a screen opened from the
                // sidebar starts beside where the sidebar opens, leaving a bare strip down the left.
                .frame(width: 1920, height: 1080)
            if let item = page.item {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 56) {
                        header(item)
                        if !page.albums.isEmpty {
                            VStack(alignment: .leading, spacing: 28) {
                                Text("Albums")
                                    .font(.marquee(40, weight: .bold))
                                    .foregroundStyle(Color.screen)
                                LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                                    ForEach(page.albums) { album in
                                        AlbumCard(item: album) { router.push(.item(album)) }
                                    }
                                }
                                .focusSection()
                            }
                        } else if page.detail != nil, !hasOutside {
                            StatusView(state: .empty("No albums yet."))
                                .frame(height: 200)
                        }
                        if let missing = extras?.missing, !missing.isEmpty {
                            VStack(alignment: .leading, spacing: 28) {
                                Text("Not in your library")
                                    .font(.marquee(40, weight: .bold))
                                    .foregroundStyle(Color.screen)
                                LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                                    ForEach(missing.listed) { entry in
                                        ExternalMusicCard(item: entry.item, showsYear: true)
                                    }
                                }
                                .focusSection()
                            }
                        }
                        if let similar = extras?.similar, !similar.isEmpty {
                            // The row runs to the screen's edges, as rows elsewhere do.
                            ExternalMusicRow(title: "Fans also like", items: similar, artists: true)
                                .padding(.horizontal, -Layout.edge)
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                    .padding(.top, 130)
                    .padding(.bottom, 90)
                }
                .scrollClipDisabled()
                .environment(\.musicRequestsPaused, paused)
            } else if let error = page.error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .task(id: model.refreshToken) { await load(ifFresh: true) }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active { Task { await load(ifFresh: true) } }
        }
    }

    private func header(_ item: ItemSummary) -> some View {
        HStack(alignment: .center, spacing: 60) {
            SquareArtwork(item: item, size: 360, circular: true)
            VStack(alignment: .leading, spacing: 22) {
                Text(item.title)
                    .font(.marquee(84, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .lineLimit(2)
                    .minimumScaleFactor(0.6)
                HStack(spacing: 22) {
                    FactsLine(facts: item.facts(scores: !model.isKids))
                    if let genres = page.detail?.genres, !genres.isEmpty {
                        Text(genres.prefix(3).joined(separator: ", "))
                            .font(.system(size: 26, weight: .medium))
                            .foregroundStyle(Color.usher)
                            .lineLimit(1)
                    }
                }
                if let overview = page.detail?.overview, !overview.isEmpty {
                    Text(overview)
                        .font(.system(size: 26))
                        .lineSpacing(4)
                        .foregroundStyle(Color.screen.opacity(0.88))
                        .lineLimit(3)
                        .frame(maxWidth: 1000, alignment: .leading)
                }
                // Play spelled out; the rest round icons, named underneath while focused (as a film's page).
                VStack(alignment: .leading, spacing: 18) {
                    HStack(spacing: 26) {
                        ForEach(actions.buttons, id: \.self) { button in
                            actionButton(button, item: item)
                        }
                        if page.isQueueing {
                            ProgressView().scaleEffect(1.2)
                        }
                    }
                    .focusSection()
                    FocusedActionName(name: focusedName ?? (paused && actions.has(.follow) ? MusicWords.lidarrNotAnswering : nil))
                }
                .padding(.top, 8)
            }
            Spacer(minLength: 0)
        }
    }

    private var paused: Bool { pausedAbove || (status?.configured == true && status?.reachable == false) }

    /// Albums of theirs outside the library, or artists like them, to fill the page when it has no albums.
    private var hasOutside: Bool { !(extras?.missing.isEmpty ?? true) || !(extras?.similar.isEmpty ?? true) }

    private var actions: MusicActions {
        // While the albums load, Play and Shuffle stand rather than appear late.
        MusicActions(kind: .artist, platform: .tv, hasMusic: page.detail == nil || !page.albums.isEmpty,
                     canAsk: extras != nil && status?.configured == true)
    }

    private var following: Bool { model.isFollowing(libraryArtist: page.id, followed: extras?.followed ?? false) }

    private var focusedName: String? {
        switch focusedAction {
        case .shuffle: "Shuffle"
        case .radio: "Radio"
        case .follow: following ? "Following" : "Follow"
        default: nil
        }
    }

    @ViewBuilder
    private func actionButton(_ button: MusicActions.Button, item: ItemSummary) -> some View {
        switch button {
        case .play:
            Button { Task { await model.playAll(of: page, shuffled: false, router: router) } } label: {
                Label("Play", systemImage: Symbol.play)
            }
            .buttonStyle(ActionButtonStyle(ambient: page.item?.ambientColor, prominent: true))
            .takesFocusFromLink()
            .disabled(page.albums.isEmpty)
        case .shuffle:
            Button { Task { await model.playAll(of: page, shuffled: true, router: router) } } label: {
                Label("Shuffle", systemImage: Symbol.shuffle)
            }
            .buttonStyle(ActionButtonStyle(ambient: ambient, iconOnly: true))
            .focused($focusedAction, equals: .shuffle)
            .disabled(page.albums.isEmpty)
        case .radio:
            Button { Task { await model.playArtist(item, radio: true, router: router) } } label: {
                Label("Radio", systemImage: Symbol.radio)
            }
            .buttonStyle(ActionButtonStyle(ambient: ambient, iconOnly: true))
            .focused($focusedAction, equals: .radio)
            .disabled(page.albums.isEmpty)
        case .follow:
            // Always there with music requests on; the page's first button for a placeholder (nothing to play yet).
            FollowArtistButton(name: item.title, following: following, libraryId: page.id, deezerId: extras?.deezerId,
                               ambient: ambient, takesLinkFocus: !actions.has(.play), focus: $focusedAction) {
                // A placeholder leaves the library when unfollowed: so does its page.
                if page.albums.isEmpty, page.detail != nil { dismiss() }
            }
        case .download, .request, .more:
            EmptyView()
        }
    }

    private func load(ifFresh: Bool = false) async {
        guard let client = model.client else { return }
        async let outside = loadExtras(client)
        await page.load(client: client, onlyIfStale: ifFresh)
        await outside
    }

    /// Off (kids, no Lidarr, an older server) leaves the page as it was, without Follow.
    private func loadExtras(_ client: FlexarrClient) async {
        guard !model.isKids else { return }
        let found = await model.musicRequestStatus()
        status = found
        guard found.configured else {
            extras = nil
            return
        }
        if let more = try? await client.musicArtistExtras(libraryItemId: page.id) { extras = more }
    }
}
