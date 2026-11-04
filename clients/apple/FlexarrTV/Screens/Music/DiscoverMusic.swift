import FlexarrKit
import SwiftUI

/// Discover › Music: rows of artists and albums the household doesn't have (from Deezer), genre tiles, and a search
/// for any artist or album. Requests go to Lidarr.
struct DiscoverMusicContent: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    /// Held by Discover, so it can pass focus to the first card on arrival.
    @Binding var discover: MusicDiscover?
    /// Lidarr isn't answering: one quiet line says so, and requests and follows wait.
    var paused = false
    @State private var error: String?
    @State private var searching = false
    var focus: FocusState<String?>.Binding?

    var body: some View {
        VStack(alignment: .leading, spacing: 50) {
            if paused {
                Text(MusicWords.lidarrNotAnswering)
                    .font(.system(size: 27, weight: .medium))
                    .foregroundStyle(Color.usher)
                    .padding(.horizontal, Layout.edge)
            }
            Button { searching = true } label: {
                Label(t(Words.search), systemImage: Symbol.search)
            }
            .buttonStyle(PillButtonStyle(selected: false))
            .padding(.horizontal, Layout.edge)
            .focusSection()
            if let discover {
                ForEach(discover.rows.filter { !$0.items.isEmpty }) { row in
                    ExternalMusicRow(title: row.title, items: row.items, artists: row.isArtists, focus: focus, focusPrefix: "music-\(row.id)")
                }
                if !discover.genres.isEmpty {
                    let genres = discover.genres
                    VStack(alignment: .leading, spacing: 30) {
                        RowHeading(title: "Genres")
                        // Not lazy: the grid starts below the screen, and Down can only move to tiles that are there.
                        Grid(alignment: .topLeading, horizontalSpacing: GenreTile.spacing, verticalSpacing: 44) {
                            ForEach(Array(stride(from: 0, to: genres.count, by: GenreTile.across)), id: \.self) { start in
                                GridRow {
                                    ForEach(genres[start..<min(start + GenreTile.across, genres.count)]) { genre in
                                        GenreTile(name: genre.name, caption: "", backdrop: genre.image) { router.push(.musicGenre(genre)) }
                                    }
                                }
                            }
                        }
                        .padding(.horizontal, Layout.edge)
                        .focusSection()
                    }
                }
                Text(t("Data from Deezer"))
                    .font(.system(size: 21, weight: .medium))
                    .foregroundStyle(Color.usher)
                    .padding(.horizontal, Layout.edge)
            } else if let error {
                StatusView(state: .failed(error) { Task { await load() } }).frame(height: 500)
            } else {
                StatusView(state: .loading).frame(height: 500)
            }
        }
        .fullScreenCover(isPresented: $searching) {
            TabStack { DiscoverMusicSearchView().environment(\.musicRequestsPaused, paused) }
                .background(Color.room.ignoresSafeArea())
                .presentationBackground(Color.room)
        }
        .environment(\.musicRequestsPaused, paused)
        // Again on coming back, so what was just requested says so.
        .task { await load() }
    }

    /// The first card, for Discover to pass focus to on arrival.
    static func firstFocusID(_ discover: MusicDiscover?) -> String? {
        guard let row = discover?.rows.first(where: { !$0.items.isEmpty }), let first = row.items.first else { return nil }
        return ExternalMusicRow.focusID("music-\(row.id)", MusicListEntry(offset: 0, item: first))
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            discover = try await client.musicDiscover()
            error = nil
        } catch {
            if discover == nil { self.error = ConnectionError(error).message }
        }
    }
}

/// Any artist or album, beyond the library: keyboard first, then artists and albums.
struct DiscoverMusicSearchView: View {
    @Environment(AppModel.self) private var model
    @State private var query = ""
    @State private var results: MusicSearchResults?
    @State private var searched = ""
    @State private var error: String?

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 30) {
                if let results {
                    if results.isEmpty {
                        StatusView(state: .empty("Nothing found for \u{201C}\(searched)\u{201D}."))
                            .frame(height: 300)
                    } else {
                        if !results.artists.isEmpty { ExternalMusicRow(title: "Artists", items: results.artists, artists: true) }
                        if !results.albums.isEmpty { ExternalMusicRow(title: "Albums", items: results.albums, artists: false) }
                    }
                } else if let error {
                    StatusView(state: .failed(error) { Task { await search() } })
                        .frame(height: 300)
                }
            }
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .ignoresSafeArea(edges: .horizontal)
        .background(RoomBackground(ambient: nil))
        .searchable(text: $query, prompt: "Any artist or album")
        .task(id: query) {
            try? await Task.sleep(for: .milliseconds(350))
            guard !Task.isCancelled else { return }
            await search()
        }
    }

    private func search() async {
        let text = query.trimmingCharacters(in: .whitespaces)
        guard text.count >= 2, let client = model.client else {
            results = nil
            return
        }
        do {
            let found = try await client.searchMusic(text)
            guard text == query.trimmingCharacters(in: .whitespaces) else { return }
            results = found
            searched = text
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}

/// A genre's artists, from outside the library.
struct MusicGenreView: View {
    @Environment(AppModel.self) private var model
    let genre: MusicGenre
    @State private var detail: MusicGenreDetail?
    @State private var error: String?

    private let columns = Array(repeating: GridItem(.fixed(250), spacing: 50, alignment: .top), count: 6)

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 40) {
                Text(genre.name).font(.marquee(72, weight: .heavy)).foregroundStyle(Color.screen)
                if let detail {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                        ForEach(detail.artists.listed) { entry in
                            ExternalMusicCard(item: entry.item, width: 220)
                        }
                    }
                    .focusSection()
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 400)
                } else {
                    ProgressView().frame(maxWidth: .infinity).padding(60)
                }
            }
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .background(RoomBackground(ambient: nil))
        .task { await load() }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            detail = try await client.musicGenre(genre.id)
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}
