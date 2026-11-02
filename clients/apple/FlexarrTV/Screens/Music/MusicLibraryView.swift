import FlexarrKit
import SwiftUI

/// A music library's Browse, as a film or TV library's: a hero led by the album played last (servers from 0.59), the
/// server's rows in the profile's order (Settings › Home & browse › Music), then Your artists, whose See all opens
/// Artists. When Browse fails, the playlists and the albums that arrived most recently instead. On the library's tab
/// the Browse, Artists, Albums and Genres switch sits at the top; pushed (an older link), the library's name does.
struct MusicLibraryView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @State private var browse: MusicBrowseModel
    /// The tab's switch; nil when pushed.
    private let view: Binding<MusicOpening>?
    private let offersAlbums: Bool
    @State private var isVisible = false
    @State private var featuredIndex = 0
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.resetFocus) private var resetFocus
    @Environment(\.focusesContent) private var arriving
    @Namespace private var page
    @FocusState private var focusedCard: String?
    /// Where across a row focus last was, so up and down keep to it (see `RowFocus`).
    @State private var column = 0


    init(library: Library, view: Binding<MusicOpening>? = nil, offersAlbums: Bool = true) {
        _browse = State(initialValue: MusicBrowseModel(library: library))
        self.view = view
        self.offersAlbums = offersAlbums
    }

    private var featured: ItemSummary? {
        browse.featured.isEmpty ? nil : browse.featured[featuredIndex % browse.featured.count]
    }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 44) {
                if !browse.featured.isEmpty {
                    HomeHero(items: browse.featured, overviews: browse.hero?.overviews ?? [:], index: $featuredIndex,
                             resuming: { browse.hero?.resuming($0) }, top: view.map { AnyView(viewSwitch($0)) })
                        .padding(.bottom, -14)
                } else {
                    header
                        .padding(.horizontal, Layout.edge)
                }
                Group {
                    if let error = browse.error, browse.artists.isEmpty {
                        StatusView(state: .failed(error) { Task { await load() } })
                            .frame(height: 500)
                    } else if browse.loadedAt == nil {
                        StatusView(state: .loading)
                            .frame(height: 500)
                    } else if browse.isEmpty {
                        StatusView(state: .empty("Nothing in this library yet."))
                            .frame(height: 500)
                    } else {
                        rows
                    }
                }
                .padding(.horizontal, Layout.edge)
                .prefersDefaultFocus(browse.featured.isEmpty, in: page)
            }
            .padding(.bottom, 80)
            .focusScope(page)
        }
        .scrollClipDisabled()
        .ignoresSafeArea(edges: view != nil || !browse.featured.isEmpty ? [.top, .horizontal] : [.horizontal])
        .background(RoomBackground(ambient: (featured ?? browse.ambientItem)?.glowColor, intensity: featured == nil ? 0.18 : 0.28)
            .animation(.easeInOut(duration: 0.8), value: featured?.id))
        .task {
            // Coming back from an artist or another tab runs this again: keep what's loaded unless it's getting old.
            // Music played since (from the hero, say) may have moved Resume on.
            let played = browse.playingWhenLoaded != model.music.current?.id
            if browse.loadedAt != nil, !Freshness.isStale(browse.loadedAt), !played { return }
            let first = browse.loadedAt == nil
            await load()
            // Opened from the sidebar, the page had nothing to focus until it loaded, so focus went nowhere and the
            // remote did nothing: give it to the first card now there is one (the hero takes it itself).
            if first, isVisible, browse.featured.isEmpty { resetFocus(in: page) }
        }
        // From the switch as the tab opens, with no hero to take it: the first card.
        .onChange(of: arriving.wrappedValue) { _, now in
            guard now, browse.featured.isEmpty, browse.loadedAt != nil else { return }
            arriving.wrappedValue = false
            resetFocus(in: page)
        }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible, Freshness.isStale(browse.loadedAt) { Task { await load() } }
        }
    }

    private func viewSwitch(_ view: Binding<MusicOpening>) -> some View {
        MusicViewSwitch(view: view, offersAlbums: offersAlbums)
    }

    @ViewBuilder
    private var header: some View {
        if let view {
            viewSwitch(view)
                .focusSection()
                .padding(.top, LibraryViewSwitch.topInset)
        } else {
            Text(browse.library.name)
                .font(.marquee(76, weight: .heavy))
                .foregroundStyle(Color.screen)
                .padding(.top, 20)
        }
    }

    @ViewBuilder
    private var rows: some View {
        VStack(alignment: .leading, spacing: 44) {
            ForEach(browse.rows) { MusicRowView(row: $0, column: $column) { Task { await load() } } }
            if browse.rows.isEmpty, !browse.playlists.isEmpty {
                fallbackRow("Your playlists", ids: browse.playlists.map(\.id)) {
                    ForEach(browse.playlists) { playlist in
                        PlaylistCard(playlist: playlist, focus: $focusedCard) { router.push(.playlist(playlist)) }
                    }
                }
            }
            if browse.rows.isEmpty, !browse.recentAlbums.isEmpty {
                fallbackRow("New albums", ids: browse.recentAlbums.map(\.id)) {
                    ForEach(browse.recentAlbums) { album in
                        AlbumCard(item: album, showsArtist: true, focus: $focusedCard) { router.push(.item(album)) }
                    }
                }
            }
            if !browse.artists.isEmpty {
                let artists = Array(browse.artists.prefix(MusicBrowseModel.artistsInRow))
                // See all only when there are more than the row shows; it opens every artist over Browse, so Menu
                // comes back here, as a film row's See all does.
                let more = browse.total > artists.count
                fallbackRow("Your artists", ids: artists.map(\.id) + (more ? ["see-all-artists"] : [])) {
                    ForEach(artists) { artist in
                        ArtistCard(item: artist, focus: $focusedCard) { router.push(.item(artist)) }
                    }
                    if more {
                        SeeAllArtistsCard(focus: $focusedCard) { router.push(.artists(browse.library)) }
                    }
                }
            }
        }
    }

    private func fallbackRow<Cards: View>(_ title: String, ids: [String], @ViewBuilder cards: () -> Cards) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(title)
                .font(.marquee(40, weight: .bold))
                .foregroundStyle(Color.screen)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) { cards() }
                    .padding(.top, 28)
                    .padding(.bottom, 40)
            }
            .scrollClipDisabled()
            .focusSection()
            .modifier(RowFocus(ids: ids, column: $column, focus: $focusedCard))
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        browse.playingWhenLoaded = model.music.current?.id
        await browse.load(client: client)
    }
}

/// The end of Your artists: every artist, in the Artists view.
private struct SeeAllArtistsCard: View {
    var focus: FocusState<String?>.Binding
    let action: () -> Void

    var body: some View {
        VStack(spacing: 18) {
            Button(action: action) {
                VStack(spacing: 12) {
                    Image(systemName: Symbol.libraries).font(.system(size: 44, weight: .semibold))
                    Text("See all").font(.system(size: 26, weight: .semibold))
                }
                .foregroundStyle(Color.screen.opacity(0.85))
                .frame(width: 220, height: 220)
                .background(Color.seat)
                .clipShape(Circle())
            }
            .buttonStyle(CircleCardStyle())
            .accessibilityLabel("See all artists")
            .modifier(SharedFocus(binding: focus, id: "see-all-artists"))
        }
    }
}

/// One of a music library's Browse rows: playlists and mixes, albums that open, or tracks that play (the one pressed
/// first, the rest of the row after it). No "See all": its links lead back to this page.
private struct MusicRowView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let row: HomeRow
    @Binding var column: Int
    @FocusState private var focusedCard: String?
    /// Loads the page again, when a mix in the row turns out to be yesterday's.
    let onStale: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(row.title)
                .font(.marquee(40, weight: .bold))
                .foregroundStyle(Color.screen)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                    ForEach(row.playlists ?? []) { playlist in
                        PlaylistCard(playlist: playlist, focus: $focusedCard) { router.push(.playlist(playlist)) }
                    }
                    ForEach(row.mixes ?? []) { MixCard(mix: $0, focus: $focusedCard, onStale: onStale) }
                    ForEach(Array(row.items.enumerated()), id: \.element.id) { index, item in
                        if row.style == .tracks || item.kind == .track {
                            TrackCard(item: item, focus: $focusedCard) { model.playTracks(row.items, from: index) }
                        } else if item.kind == .artist {
                            ArtistCard(item: item, focus: $focusedCard) { router.push(.item(item)) }
                        } else {
                            AlbumCard(item: item, showsArtist: true, focus: $focusedCard) { router.push(.item(item)) }
                        }
                    }
                }
                .padding(.top, 28)
                .padding(.bottom, 40)
            }
            .scrollClipDisabled()
            .focusSection()
            .modifier(RowFocus(ids: ids, column: $column, focus: $focusedCard))
        }
    }

    /// The row's cards in order, as they take focus.
    private var ids: [String] { (row.playlists ?? []).map(\.id) + (row.mixes ?? []).map(\.id) + row.items.map(\.id) }
}
