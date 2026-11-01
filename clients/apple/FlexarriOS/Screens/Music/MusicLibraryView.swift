import FlexarrKit
import SwiftUI

/// A music library: the server's Browse rows in the profile's order (Settings › Home & browse › Music), then every
/// artist as a round picture. When Browse fails, the playlists and the albums that arrived most recently instead.
struct MusicLibraryView: View {
    @Environment(AppModel.self) private var model
    @State private var browse: MusicBrowseModel
    @State private var isVisible = false
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.horizontalSizeClass) private var sizeClass

    init(library: Library) {
        _browse = State(initialValue: MusicBrowseModel(library: library))
    }

    private var columns: [GridItem] {
        let minimum: CGFloat = sizeClass == .regular ? 140 : 100
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.45), spacing: 12, alignment: .top)]
    }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 24) {
                Text(browse.total == 1 ? "1 artist" : "\(browse.total.formatted()) artists")
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(Color.usher)
                    .monospacedDigit()
                    .padding(.horizontal, Layout.edge)
                    .opacity(browse.artists.isEmpty ? 0 : 1)
                if let error = browse.error, browse.artists.isEmpty {
                    StatusView(state: .failed(error) { Task { await load() } })
                        .frame(height: 400)
                } else if browse.isEmpty {
                    StatusView(state: .empty("Nothing in this library yet."))
                        .frame(height: 400)
                } else {
                    if !browse.rows.isEmpty {
                        ForEach(browse.rows) { MusicRowView(row: $0, width: cardWidth) }
                    }
                    if browse.rows.isEmpty, !browse.playlists.isEmpty {
                        VStack(alignment: .leading, spacing: 10) {
                            SectionHeading(title: "Your playlists")
                            ScrollView(.horizontal) {
                                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                                    ForEach(browse.playlists) { playlist in
                                        PlaylistCard(playlist: playlist, width: cardWidth)
                                    }
                                }
                                .padding(.horizontal, Layout.edge)
                            }
                            .scrollIndicators(.hidden)
                        }
                    }
                    if browse.rows.isEmpty, !browse.recentAlbums.isEmpty {
                        VStack(alignment: .leading, spacing: 10) {
                            SectionHeading(title: "New albums")
                            ScrollView(.horizontal) {
                                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                                    ForEach(browse.recentAlbums) { album in
                                        AlbumCard(item: album, width: cardWidth, showsArtist: true)
                                    }
                                }
                                .padding(.horizontal, Layout.edge)
                            }
                            .scrollIndicators(.hidden)
                        }
                    }
                    artistGrid
                    if browse.isLoading {
                        ProgressView().frame(maxWidth: .infinity).padding(20)
                    }
                }
            }
            .padding(.top, 8)
            .padding(.bottom, 24)
        }
        .background(RoomBackground(ambient: browse.ambientItem?.glowColor, intensity: 0.18))
        .navigationTitle(browse.library.name)
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

    private var artistGrid: some View {
        VStack(alignment: .leading, spacing: 12) {
            SectionHeading(title: "Artists")
            LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                ForEach(Array(browse.artists.enumerated()), id: \.element.id) { index, artist in
                    ArtistCard(item: artist)
                        .onAppear { if let client = model.client { browse.loadMoreIfNeeded(at: index, client: client) } }
                }
            }
            .padding(.horizontal, Layout.edge)
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

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: row.title)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                    ForEach(row.playlists ?? []) { PlaylistCard(playlist: $0, width: width) }
                    ForEach(row.mixes ?? []) { MixCard(mix: $0, width: width) }
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
