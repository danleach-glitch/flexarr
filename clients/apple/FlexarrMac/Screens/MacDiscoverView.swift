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
    @State private var loadedAt: Date?
    @State private var error: String?
    /// Why a part couldn't load or a search failed: said, with Try again, rather than shown as nothing found.
    @State private var titlesError: String?
    @State private var musicError: String?
    @State private var searchError: String?

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
        .pageSearch($query, prompt: showing == .titles ? t("Search for a movie or show") : t("Search for an artist or album"))
        .pageSwitch($mode, offersTitles && offersMusic ? [(.titles, t(Words.moviesAndShows)), (.music, t(Words.music))] : [])
        // Again on coming back, so what was just requested says so.
        .task { await load() }
        .task(id: "\(showing)#\(words)") { await search() }
    }

    // MARK: Browsing

    @ViewBuilder
    private var titleRows: some View {
        if rows.isEmpty, let titlesError {
            StatusView(state: .failed(titlesError) { Task { await reload() } }).frame(height: 400)
        } else if rows.isEmpty {
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
        } else if let musicError {
            StatusView(state: .failed(musicError) { Task { await reload() } }).frame(height: 400)
        } else if music != nil {
            StatusView(state: .empty(t("Nothing to discover right now."))).frame(height: 400)
        } else {
            StatusView(state: .loading).frame(height: 400)
        }
    }

    // MARK: Searching

    @ViewBuilder
    private var results: some View {
        if let searchError, !searching {
            StatusView(state: .failed(searchError) { Task { await search() } }).frame(height: 300)
        } else if searching && titleResults.isEmpty && musicResults.isEmpty {
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
            searchError = nil
            return
        }
        // A pause in typing before asking.
        guard (try? await Task.sleep(for: .milliseconds(350))) != nil else { return }
        searching = true
        defer { searching = false }
        do {
            if showing == .titles {
                let found = try await client.searchEverything(words)
                guard !Task.isCancelled else { return }
                titleResults = found
            } else {
                let found = try await client.searchMusic(words)
                guard !Task.isCancelled else { return }
                musicResults = found
            }
            searchError = nil
        } catch {
            // The next keystroke's search took over: its answer is the one to show.
            guard !Task.isCancelled, !(error is CancellationError) else { return }
            searchError = ConnectionError(error).message
        }
    }

    /// Try again: fetches now, however fresh.
    private func reload() async {
        loadedAt = nil
        loaded = false
        await load()
    }

    /// Movies and shows and Music, each on its own: one failing doesn't hold the other back. Coming back to the page
    /// fetches again only once what's shown is old.
    private func load() async {
        guard let client = model.client, !loaded || Freshness.isStale(loadedAt) else { return }
        async let titles: [ExternalRow]? = offersTitles ? client.discoverRows().filter { !$0.items.isEmpty } : []
        async let found: MusicDiscover? = offersMusic ? client.musicDiscover() : nil
        do {
            rows = try await titles ?? []
            error = nil
            titlesError = nil
        } catch {
            if !loaded, !offersMusic { self.error = ConnectionError(error).message }
            titlesError = ConnectionError(error).message
        }
        do {
            music = try await found ?? .empty
            musicError = nil
        } catch {
            musicError = ConnectionError(error).message
            if music == nil { music = .empty }
        }
        loaded = true
        loadedAt = Date()
    }
}
