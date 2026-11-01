import FlexarrKit
import SwiftUI

/// A music library: the server's Browse rows in the profile's order (Settings › Home & browse › Music), then every
/// artist as a round picture. When Browse fails, the playlists and the albums that arrived most recently instead.
struct MusicLibraryView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @State private var browse: MusicBrowseModel
    /// Off on the library's tab, whose lit name in the sidebar already says where you are.
    private let showsTitle: Bool
    @State private var isVisible = false
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.resetFocus) private var resetFocus
    @Namespace private var page
    @FocusState private var focusedCard: String?
    /// Where across a row focus last was, so up and down keep to it (see `RowFocus`).
    @State private var column = 0

    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    init(library: Library, showsTitle: Bool = true) {
        _browse = State(initialValue: MusicBrowseModel(library: library))
        self.showsTitle = showsTitle
    }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 44) {
                header
                if let error = browse.error, browse.artists.isEmpty {
                    StatusView(state: .failed(error) { Task { await load() } })
                        .frame(height: 500)
                } else if browse.isEmpty {
                    StatusView(state: .empty("Nothing in this library yet."))
                        .frame(height: 500)
                } else {
                    ForEach(browse.rows) { MusicRowView(row: $0, column: $column) { Task { await load() } } }
                    if browse.rows.isEmpty, !browse.playlists.isEmpty {
                        VStack(alignment: .leading, spacing: 0) {
                            Text("Your playlists")
                                .font(.marquee(40, weight: .bold))
                                .foregroundStyle(Color.screen)
                            ScrollView(.horizontal) {
                                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                                    ForEach(browse.playlists) { playlist in
                                        PlaylistCard(playlist: playlist, focus: $focusedCard) { router.push(.playlist(playlist)) }
                                    }
                                }
                                .padding(.top, 28)
                                .padding(.bottom, 40)
                            }
                            .scrollClipDisabled()
                            .focusSection()
                            .modifier(RowFocus(ids: browse.playlists.map(\.id), column: $column, focus: $focusedCard))
                        }
                    }
                    if browse.rows.isEmpty, !browse.recentAlbums.isEmpty {
                        VStack(alignment: .leading, spacing: 0) {
                            Text("New albums")
                                .font(.marquee(40, weight: .bold))
                                .foregroundStyle(Color.screen)
                            ScrollView(.horizontal) {
                                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                                    ForEach(browse.recentAlbums) { album in
                                        AlbumCard(item: album, showsArtist: true, focus: $focusedCard) { router.push(.item(album)) }
                                    }
                                }
                                .padding(.top, 28)
                                .padding(.bottom, 40)
                            }
                            .scrollClipDisabled()
                            .focusSection()
                            .modifier(RowFocus(ids: browse.recentAlbums.map(\.id), column: $column, focus: $focusedCard))
                        }
                    }
                    VStack(alignment: .leading, spacing: 28) {
                        Text("Artists")
                            .font(.marquee(40, weight: .bold))
                            .foregroundStyle(Color.screen)
                        LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                            ForEach(Array(browse.artists.enumerated()), id: \.element.id) { index, artist in
                                ArtistCard(item: artist, focus: $focusedCard) { router.push(.item(artist)) }
                                    .onAppear { if let client = model.client { browse.loadMoreIfNeeded(at: index, client: client) } }
                            }
                        }
                        .focusSection()
                        .modifier(RowFocus(ids: browse.artists.map(\.id), column: $column, focus: $focusedCard, columns: 6))
                    }
                    if browse.isLoading {
                        ProgressView().frame(maxWidth: .infinity).padding(40)
                    }
                }
            }
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 80)
            .focusScope(page)
        }
        .scrollClipDisabled()
        .background(RoomBackground(ambient: browse.ambientItem?.glowColor, intensity: 0.18))
        .task {
            // Coming back from an artist or another tab runs this again: keep what's loaded unless it's getting old.
            if browse.loadedAt != nil, !Freshness.isStale(browse.loadedAt) { return }
            let first = browse.loadedAt == nil
            await load()
            // Opened from the sidebar, the page had nothing to focus until it loaded, so focus went nowhere and the
            // remote did nothing: give it to the first card now there is one.
            if first, isVisible { resetFocus(in: page) }
        }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible, Freshness.isStale(browse.loadedAt) { Task { await load() } }
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 4) {
            if showsTitle {
                Text(browse.library.name)
                    .font(.marquee(76, weight: .heavy))
                    .foregroundStyle(Color.screen)
            }
            Text(browse.total == 1 ? "1 artist" : "\(browse.total.formatted()) artists")
                .font(.system(size: 24, weight: .medium))
                .foregroundStyle(Color.usher)
                .monospacedDigit()
                .opacity(browse.artists.isEmpty ? 0 : 1)
        }
        .padding(.top, 20)
    }

    private func load() async {
        guard let client = model.client else { return }
        await browse.load(client: client)
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
