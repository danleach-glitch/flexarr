import FlexarrKit
import SwiftUI

/// A music library's Browse, as a film or TV library's: a hero led by the album played last (servers from 0.59), the
/// server's rows in the profile's order (Settings › Home & browse › Music), then Your artists, whose See all opens
/// Artists. When Browse fails, the playlists and the albums that arrived most recently instead.
struct MusicLibraryView<Header: View>: View {
    @Environment(AppModel.self) private var model
    @State private var browse: MusicBrowseModel
    @State private var isVisible = false
    @State private var featuredIndex = 0
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.horizontalSizeClass) private var sizeClass
    /// Opens the library's Artists view.
    private let showArtists: () -> Void
    /// The Browse · Artists · Albums · Genres switch, at the top.
    private let header: Header

    /// How many artists Your artists shows before its See all.
    private static var artistsInRow: Int { 20 }

    init(library: Library, showArtists: @escaping () -> Void, @ViewBuilder header: () -> Header) {
        _browse = State(initialValue: MusicBrowseModel(library: library))
        self.showArtists = showArtists
        self.header = header()
    }

    private var featured: ItemSummary? {
        browse.featured.isEmpty ? nil : browse.featured[featuredIndex % browse.featured.count]
    }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 24) {
                header
                if !browse.featured.isEmpty {
                    HomeHero(items: browse.featured, overviews: browse.hero?.overviews ?? [:], index: $featuredIndex,
                             resuming: { browse.hero?.resuming($0) })
                        .padding(.bottom, -12)
                }
                if let error = browse.error, browse.artists.isEmpty {
                    StatusView(state: .failed(error) { Task { await load() } })
                        .frame(height: 400)
                } else if browse.isEmpty {
                    StatusView(state: .empty("Nothing in this library yet."))
                        .frame(height: 400)
                } else {
                    if !browse.rows.isEmpty {
                        ForEach(browse.rows) { MusicRowView(row: $0, width: cardWidth) { Task { await load() } } }
                    }
                    if browse.rows.isEmpty, !browse.playlists.isEmpty {
                        row("Your playlists") {
                            ForEach(browse.playlists) { playlist in
                                PlaylistCard(playlist: playlist, width: cardWidth)
                            }
                        }
                    }
                    if browse.rows.isEmpty, !browse.recentAlbums.isEmpty {
                        row("New albums") {
                            ForEach(browse.recentAlbums) { album in
                                AlbumCard(item: album, width: cardWidth, showsArtist: true)
                            }
                        }
                    }
                    if !browse.artists.isEmpty {
                        row("Your artists", action: showArtists) {
                            ForEach(browse.artists.prefix(Self.artistsInRow)) { ArtistCard(item: $0, width: cardWidth * 0.85) }
                        }
                    }
                }
            }
            .padding(.top, 8)
            .padding(.bottom, 24)
        }
        .background(RoomBackground(ambient: (featured ?? browse.ambientItem)?.glowColor, intensity: featured == nil ? 0.18 : 0.28)
            .animation(.easeInOut(duration: 0.8), value: featured?.id))
        .refreshable { await load() }
        .task {
            if browse.loadedAt != nil, !Freshness.isStale(browse.loadedAt) { return }
            await load()
        }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible, Freshness.isStale(browse.loadedAt) { Task { await load() } }
        }
    }

    private var cardWidth: CGFloat { Layout.posterWidth(sizeClass) * 1.15 }

    private func row<Cards: View>(_ title: String, action: (() -> Void)? = nil, @ViewBuilder cards: () -> Cards) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: title, action: action)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) { cards() }
                    .padding(.horizontal, Layout.edge)
            }
            .scrollIndicators(.hidden)
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        await browse.load(client: client)
    }
}

/// One of a music library's Browse rows: playlists and mixes, albums that open, or tracks that play (the one tapped
/// first, the rest of the row after it). No "See all": its links lead back to this page.
private struct MusicRowView: View {
    @Environment(AppModel.self) private var model
    let row: HomeRow
    let width: CGFloat
    /// Loads the page again, when a mix in the row turns out to be yesterday's.
    let onStale: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: row.title)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                    ForEach(row.playlists ?? []) { PlaylistCard(playlist: $0, width: width) }
                    ForEach(row.mixes ?? []) { MixCard(mix: $0, width: width, onStale: onStale) }
                    ForEach(Array(row.items.enumerated()), id: \.element.id) { index, item in
                        if row.style == .tracks || item.kind == .track {
                            TrackCard(item: item, width: width) { model.playTracks(row.items, from: index) }
                        } else if item.kind == .artist {
                            ArtistCard(item: item, width: width)
                        } else {
                            AlbumCard(item: item, width: width, showsArtist: true)
                        }
                    }
                }
                .padding(.horizontal, Layout.edge)
            }
            .scrollIndicators(.hidden)
        }
    }
}
