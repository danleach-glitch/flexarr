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
    /// The Browse · Artists · Albums · Genres switch, at the top.
    private let header: Header

    @Environment(Router.self) private var router

    init(library: Library, @ViewBuilder header: () -> Header) {
        _browse = State(initialValue: MusicBrowseModel(library: library))
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
                } else if browse.loadedAt == nil {
                    StatusView(state: .loading)
                        .frame(height: 400)
                } else if browse.isEmpty {
                    StatusView(state: .empty(t("Nothing in this library yet.")))
                        .frame(height: 400)
                } else {
                    if !browse.rows.isEmpty {
                        ForEach(browse.rows) { MusicRowView(row: $0, library: browse.library, width: cardWidth) { Task { await load() } } }
                    }
                    if browse.rows.isEmpty, !browse.playlists.isEmpty {
                        row(t("Your playlists")) {
                            ForEach(browse.playlists) { playlist in
                                PlaylistCard(playlist: playlist, width: cardWidth)
                            }
                        }
                    }
                    if browse.rows.isEmpty, !browse.recentAlbums.isEmpty {
                        row(t("New albums")) {
                            ForEach(browse.recentAlbums) { album in
                                AlbumCard(item: album, width: cardWidth, showsArtist: true)
                            }
                        }
                    }
                    if !browse.artists.isEmpty {
                        // See all only when there are more than the row shows; it opens Artists over Browse.
                        row(t(Words.yourArtists), action: browse.total > MusicBrowseModel.artistsInRow ? { router.push(.artists(browse.library)) } : nil,
                            actionLabel: t("See all artists")) {
                            ForEach(browse.artists.prefix(MusicBrowseModel.artistsInRow)) { ArtistCard(item: $0, width: cardWidth * 0.85) }
                        }
                    }
                }
            }
            .padding(.top, 8)
            .padding(.bottom, 24)
        }
        .background(RoomBackground(ambient: (featured ?? browse.ambientItem)?.glowColor, intensity: featured == nil ? 0.18 : 0.28)
            .animation(.easeInOut(duration: 0.8), value: featured?.id))
        // In a task of its own: SwiftUI cancels a refresh's task when what it loads redraws the page, partway through.
        .refreshable { await Task { await load() }.value }
        .task {
            // Music played since (from the hero, say) may have moved Resume on.
            let played = browse.playingWhenLoaded != model.music.current?.id
            if browse.completedAt != nil, !Freshness.isStale(browse.completedAt), !played { return }
            await load()
        }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible, Freshness.isStale(browse.completedAt) { Task { await load() } }
        }
    }

    private var cardWidth: CGFloat { Layout.posterWidth(sizeClass) * 1.15 }

    private func row<Cards: View>(_ title: String, action: (() -> Void)? = nil, actionLabel: String? = nil, @ViewBuilder cards: () -> Cards) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: title, action: action, actionLabel: actionLabel)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) { cards() }
                    .padding(.horizontal, Layout.edge)
            }
            .scrollIndicators(.hidden)
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        browse.playingWhenLoaded = model.music.current?.id
        await browse.load(client: client)
    }
}

/// One of a music library's Browse rows: playlists and mixes, albums that open, or tracks that play (the one tapped
/// first, the rest of the row after it). See all only where the row leads to Albums (New albums, newest first).
private struct MusicRowView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let row: HomeRow
    let library: Library
    let width: CGFloat
    /// Loads the page again, when a mix in the row turns out to be yesterday's.
    let onStale: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: row.title, action: row.albumsSort.map { sort in { router.push(.albums(library, sort)) } },
                           actionLabel: t("See all albums"))
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
