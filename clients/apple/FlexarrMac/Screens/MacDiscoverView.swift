import FlexarrKit
import SwiftUI

/// Discover on the Mac, as the web and the Apple TV have it: Movies and shows (what's trending and popular, from
/// Seerr) and Music (artists and albums the household doesn't have, from Lidarr's Deezer), each with a search for
/// anything to request. The switch is in the toolbar, the search field in its middle.
struct MacDiscoverView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    let offersTitles: Bool
    let offersMusic: Bool

    enum Mode: Hashable { case titles, music }

    @State private var mode: Mode = .titles
    @State private var query = ""
    @State private var rows: [ExternalRow] = []
    @State private var music: MusicDiscover?
    @State private var titleResults: [ExternalTitle] = []
    @State private var musicResults = MusicSearchResults.empty
    @State private var searching = false
    @State private var loaded = false
    @State private var error: String?

    private var showing: Mode { offersTitles ? (offersMusic ? mode : .titles) : .music }
    private var words: String { query.trimmingCharacters(in: .whitespaces) }

    var body: some View {
        Group {
            if !loaded, let error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else if !loaded {
                StatusView(state: .loading)
            } else {
                ScrollView(.vertical) {
                    LazyVStack(alignment: .leading, spacing: 32) {
                        if !words.isEmpty { results } else if showing == .titles { titleRows } else { musicRows }
                    }
                    .padding(.vertical, 24)
                }
            }
        }
        .background(RoomBackground(ambient: nil))
        .navigationTitle(t(Words.discover))
        .searchable(text: $query, placement: .toolbar,
                    prompt: showing == .titles ? t("Search for a movie or show") : t("Search for an artist or album"))
        .pageSwitch($mode, offersTitles && offersMusic ? [(.titles, t(Words.moviesAndShows)), (.music, t(Words.music))] : [])
        // Again on coming back, so what was just requested says so.
        .task { await load() }
        .task(id: "\(showing)#\(words)") { await search() }
    }

    // MARK: Browsing

    @ViewBuilder
    private var titleRows: some View {
        if rows.isEmpty {
            StatusView(state: .empty(t("Nothing to discover right now."))).frame(height: 400)
        }
        ForEach(rows) { row in
            VStack(alignment: .leading, spacing: 12) {
                SectionHeading(title: row.title)
                ScrollView(.horizontal) {
                    LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                        ForEach(row.items) { OutsideTitleCard(title: $0, width: Layout.posterWidth(sizeClass)) }
                    }
                    .padding(.horizontal, Layout.edge)
                }
                .scrollIndicators(.hidden)
            }
        }
    }

    @ViewBuilder
    private var musicRows: some View {
        if let music, !music.rows.isEmpty {
            ForEach(music.rows) { ExternalMusicRow(title: $0.title, items: $0.items) }
        } else if music != nil {
            StatusView(state: .empty(t("Nothing to discover right now."))).frame(height: 400)
        } else {
            StatusView(state: .loading).frame(height: 400)
        }
    }

    // MARK: Searching

    @ViewBuilder
    private var results: some View {
        if searching && titleResults.isEmpty && musicResults.isEmpty {
            StatusView(state: .loading).frame(height: 300)
        } else if showing == .titles {
            if titleResults.isEmpty {
                StatusView(state: .empty(t("Nothing found for “{query}”.", ["query": words]))).frame(height: 300)
            } else {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: Layout.posterWidth(sizeClass)), spacing: Layout.rowSpacing, alignment: .top)],
                          alignment: .leading, spacing: 20) {
                    ForEach(titleResults) { OutsideTitleCard(title: $0, width: Layout.posterWidth(sizeClass)) }
                }
                .padding(.horizontal, Layout.edge)
            }
        } else if musicResults.isEmpty {
            StatusView(state: .empty(t("Nothing found for “{query}”.", ["query": words]))).frame(height: 300)
        } else {
            if !musicResults.artists.isEmpty { ExternalMusicRow(title: t(Words.artists), items: musicResults.artists) }
            if !musicResults.albums.isEmpty { ExternalMusicRow(title: t(Words.albums), items: musicResults.albums) }
        }
    }

    private func search() async {
        guard !words.isEmpty, let client = model.client else {
            titleResults = []
            musicResults = .empty
            return
        }
        // A pause in typing before asking.
        guard (try? await Task.sleep(for: .milliseconds(350))) != nil else { return }
        searching = true
        defer { searching = false }
        if showing == .titles {
            titleResults = (try? await client.searchEverything(words)) ?? []
        } else {
            musicResults = (try? await client.searchMusic(words)) ?? .empty
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            if offersTitles { rows = try await client.discoverRows().filter { !$0.items.isEmpty } }
            error = nil
        } catch {
            if !loaded { self.error = ConnectionError(error).message }
            return
        }
        loaded = true
        if offersMusic { music = (try? await client.musicDiscover()) ?? .empty }
    }
}
