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
                    ForEach(browse.rows) { MusicRowView(row: $0) }
                    if browse.rows.isEmpty, !browse.playlists.isEmpty {
                        VStack(alignment: .leading, spacing: 0) {
                            Text("Your playlists")
                                .font(.marquee(40, weight: .bold))
                                .foregroundStyle(Color.screen)
                            ScrollView(.horizontal) {
                                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                                    ForEach(browse.playlists) { playlist in
                                        PlaylistCard(playlist: playlist) { router.push(.playlist(playlist)) }
                                    }
                                }
                                .padding(.top, 28)
                                .padding(.bottom, 40)
                            }
                            .scrollClipDisabled()
                            .focusSection()
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
                                        AlbumCard(item: album, showsArtist: true) { router.push(.item(album)) }
                                    }
                                }
                                .padding(.top, 28)
                                .padding(.bottom, 40)
                            }
                            .scrollClipDisabled()
                            .focusSection()
                        }
                    }
                    VStack(alignment: .leading, spacing: 28) {
                        Text("Artists")
                            .font(.marquee(40, weight: .bold))
                            .foregroundStyle(Color.screen)
                        LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                            ForEach(Array(browse.artists.enumerated()), id: \.element.id) { index, artist in
                                ArtistCard(item: artist) { router.push(.item(artist)) }
                                    .onAppear { if let client = model.client { browse.loadMoreIfNeeded(at: index, client: client) } }
                            }
                        }
                        .focusSection()
                    }
                    if browse.isLoading {
                        ProgressView().frame(maxWidth: .infinity).padding(40)
                    }
                }
            }
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .background(RoomBackground(ambient: browse.ambientItem?.glowColor, intensity: 0.18))
        .task {
            // Coming back from an artist or another tab runs this again: keep what's loaded unless it's getting old.
            if browse.loadedAt != nil, !Freshness.isStale(browse.loadedAt) { return }
            await load()
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

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(row.title)
                .font(.marquee(40, weight: .bold))
                .foregroundStyle(Color.screen)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                    ForEach(row.playlists ?? []) { playlist in
                        PlaylistCard(playlist: playlist) { router.push(.playlist(playlist)) }
                    }
                    ForEach(row.mixes ?? []) { MixCard(mix: $0) }
                    ForEach(Array(row.items.enumerated()), id: \.element.id) { index, item in
                        if row.style == .tracks || item.kind == .track {
                            TrackCard(item: item) { model.playTracks(row.items, from: index) }
                        } else if item.kind == .artist {
                            ArtistCard(item: item) { router.push(.item(item)) }
                        } else {
                            AlbumCard(item: item, showsArtist: true) { router.push(.item(item)) }
                        }
                    }
                }
                .padding(.top, 28)
                .padding(.bottom, 40)
            }
            .scrollClipDisabled()
            .focusSection()
        }
    }
}
