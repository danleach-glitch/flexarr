import FlexarrKit
import SwiftUI

/// Poster grid for one library, paged with skip/take, with sort and genre in the toolbar. A film or TV library also has
/// Genres: a tile for each, opening its titles in that genre.
struct LibraryView: View {
    @Environment(AppModel.self) private var model
    let library: Library
    var initialSort: LibrarySort = .title
    /// Opened from a genre's tile: just its titles, under its name.
    var initialGenre: String?

    private enum Showing: Hashable { case all, genres }
    @State private var showing = Showing.all

    @State private var sort: LibrarySort = .title
    @State private var genre: String?
    @State private var genres: [GenreCount] = []
    @State private var items: [ItemSummary] = []
    @State private var total = 0
    @State private var isLoading = false
    @State private var error: String?
    @State private var didApplyInitialSort = false
    /// `model.refreshToken` when `items` was last fetched.
    @State private var loadedToken: Int?
    @State private var loadedAt: Date?
    @State private var isVisible = false
    /// Bumped by each reload (a new sort or genre), so an answer for the old one is dropped rather than shown or appended.
    @State private var generation = 0
    @State private var isRefreshing = false
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.horizontalSizeClass) private var sizeClass

    private static let pageSize = 60
    /// The most the server sends in one page (ItemEndpoints' MaxPageSize).
    private static let maxPageSize = 500

    private var columns: [GridItem] {
        let minimum: CGFloat = sizeClass == .regular ? 140 : 100
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.45), spacing: 12, alignment: .top)]
    }

    /// Films and shows have genres to browse by; a genre's own page doesn't offer them again.
    private var hasGenres: Bool { initialGenre == nil && (library.type == .movies || library.type == .tvShows) }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 12) {
                if hasGenres {
                    Picker("Show", selection: $showing) {
                        Text(library.type == .tvShows ? "All shows" : "All films").tag(Showing.all)
                        Text("Genres").tag(Showing.genres)
                    }
                    .pickerStyle(.segmented)
                    .padding(.horizontal, Layout.edge)
                    .padding(.bottom, 4)
                }
                if showing == .genres {
                    LibraryGenreGrid(library: library, genres: genres)
                } else {
                    titles
                }
            }
            .padding(.bottom, 32)
        }
        .background(RoomBackground(ambient: items.first?.glowColor, intensity: 0.18))
        .navigationTitle(initialGenre ?? library.name)
        .toolbar {
            if showing == .all {
                ToolbarItem(placement: .topBarTrailing) { sortAndFilter }
            }
        }
        .refreshable { await reload() }
        .task {
            if !didApplyInitialSort {
                didApplyInitialSort = true
                sort = initialSort
                genre = initialGenre
            }
            // Coming back from a detail screen or another tab runs this again. Keep what's loaded, and so the
            // scroll position, only bringing it up to date if something changed here or it's getting old.
            if let loadedToken, !items.isEmpty {
                if loadedToken != model.refreshToken || Freshness.isStale(loadedAt) { await refreshLoaded() }
                return
            }
            async let genresTask = try? model.client?.libraryGenres(library.id)
            await reload()
            genres = await genresTask ?? []
        }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible, Freshness.isStale(loadedAt) { Task { await refreshLoaded() } }
        }
        .onChange(of: sort) { Task { await reload() } }
        .onChange(of: genre) { Task { await reload() } }
        .onChange(of: model.refreshToken) { Task { await refreshLoaded() } }
    }

    @ViewBuilder
    private var titles: some View {
        Text(total == 1 ? "1 title" : "\(total.formatted()) titles")
            .font(.subheadline.weight(.medium))
            .foregroundStyle(Color.usher)
            .monospacedDigit()
            .padding(.horizontal, Layout.edge)
            .opacity(items.isEmpty ? 0 : 1)
        if let error, items.isEmpty {
            StatusView(state: .failed(error) { Task { await reload() } })
                .frame(height: 400)
        } else if items.isEmpty && !isLoading {
            StatusView(state: .empty(genre.map { "No \($0) titles here." } ?? "Nothing in this library yet."))
                .frame(height: 400)
        } else {
            LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                    PosterCard(item: item)
                        .onAppear { loadMoreIfNeeded(at: index) }
                }
            }
            .padding(.horizontal, Layout.edge)
            if isLoading {
                ProgressView().frame(maxWidth: .infinity).padding(24)
            }
        }
    }

    private var sortAndFilter: some View {
        Menu {
            Picker("Sort by", selection: $sort) {
                ForEach(LibrarySort.allCases) { option in
                    Text(option.label).tag(option)
                }
            }
            if !genres.isEmpty {
                Picker("Genre", selection: $genre) {
                    Text("All genres").tag(String?.none)
                    ForEach(genres, id: \.name) { g in
                        Text("\(g.name) (\(g.count))").tag(Optional(g.name))
                    }
                }
                .pickerStyle(.menu)
            }
        } label: {
            Label("Sort and filter", systemImage: genre == nil ? "line.3.horizontal.decrease.circle" : "line.3.horizontal.decrease.circle.fill")
        }
    }

    private func reload() async {
        guard let client = model.client else { return }
        generation += 1
        let current = generation
        isLoading = true
        // A newer reload owns the spinner by now.
        defer { if current == generation { isLoading = false } }
        let token = model.refreshToken
        do {
            let page = try await client.libraryItems(library.id, sort: sort, genre: genre, skip: 0, take: Self.pageSize)
            guard current == generation else { return }
            items = page.items
            total = page.total
            loadedToken = token
            loadedAt = Date()
            error = nil
        } catch {
            if current == generation { self.error = ConnectionError(error).message }
        }
    }

    /// After playback or watched changes, or after a while, refetch what is already on screen so badges update.
    /// Only one runs at a time; one asked for while it runs because something changed again runs once it finishes.
    private func refreshLoaded() async {
        guard !isRefreshing else { return }
        isRefreshing = true
        defer { isRefreshing = false }
        var token: Int
        repeat {
            token = model.refreshToken
            await fetchLoaded()
        } while token != model.refreshToken && !Task.isCancelled
    }

    /// The server sends at most `maxPageSize` a request, so a long list comes back in several; whatever didn't
    /// (a page failed, or more were appended meanwhile) is kept as it was, so the scroll position stays put.
    private func fetchLoaded() async {
        guard let client = model.client, !items.isEmpty else { return }
        let current = generation
        let token = model.refreshToken
        let wanted = max(items.count, Self.pageSize)
        var fetched: [ItemSummary] = []
        var known = Set<ItemSummary.ID>()
        var offset = 0
        var fetchedTotal: Int?
        while offset < wanted {
            guard let page = try? await client.libraryItems(
                library.id, sort: sort, genre: genre, skip: offset, take: min(wanted - offset, Self.maxPageSize)),
                current == generation else { break }
            offset += page.items.count
            // A title added or removed between pages can shift one into the next: list it once.
            fetched.append(contentsOf: page.items.filter { known.insert($0.id).inserted })
            fetchedTotal = page.total
            if page.items.isEmpty || offset >= page.total { break }
        }
        guard current == generation, let fetchedTotal else { return }
        if offset >= fetchedTotal {
            items = fetched // That's the whole list now: anything past it has gone.
        } else {
            items = fetched + items.dropFirst(offset).filter { !known.contains($0.id) }
        }
        total = fetchedTotal
        if offset >= wanted || offset >= fetchedTotal {
            loadedToken = token
            loadedAt = Date()
        }
    }

    private func loadMoreIfNeeded(at index: Int) {
        guard !isLoading, items.count < total, index >= items.count - 18, let client = model.client else { return }
        let current = generation
        let skip = items.count
        isLoading = true
        Task {
            defer { if current == generation { isLoading = false } }
            // Dropped if the sort or genre changed (or the list was refetched) while it was out.
            if let page = try? await client.libraryItems(library.id, sort: sort, genre: genre, skip: skip, take: Self.pageSize),
               current == generation, skip == items.count {
                let known = Set(items.map(\.id))
                items.append(contentsOf: page.items.filter { !known.contains($0.id) })
                total = page.total
            }
        }
    }
}

/// A film or TV library's genres as tiles: a backdrop from one of its titles, the name and how many there are.
private struct LibraryGenreGrid: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    let library: Library
    let genres: [GenreCount]

    var body: some View {
        if genres.isEmpty {
            StatusView(state: .empty("No genres here yet. They come with each title's details once a scan has found them."))
                .frame(height: 400)
        } else {
            let minimum: CGFloat = sizeClass == .regular ? 240 : 150
            LazyVGrid(columns: [GridItem(.adaptive(minimum: minimum), spacing: 12)], spacing: 12) {
                ForEach(genres, id: \.name) { genre in
                    Button { router.push(.library(library, .title, genre: genre.name)) } label: { GenreTile(genre: genre, caption: caption(genre)) }
                        .buttonStyle(CardButtonStyle())
                        .accessibilityLabel("\(genre.name), \(caption(genre))")
                }
            }
            .padding(.horizontal, Layout.edge)
        }
    }

    private func caption(_ genre: GenreCount) -> String {
        let noun = library.type == .tvShows ? (genre.count == 1 ? "show" : "shows") : (genre.count == 1 ? "film" : "films")
        return "\(genre.count) \(noun)"
    }
}

/// A genre's tile: a backdrop from one of its titles (a wash without one), the name and how many there are.
struct GenreTile: View {
    @Environment(AppModel.self) private var model
    let genre: GenreCount
    let caption: String

    var body: some View {
        Color.seat
            .aspectRatio(16 / 9, contentMode: .fit)
            .overlay {
                RemoteImage(url: model.client?.imageURL(genre.backdrop, width: 640)) {
                    LinearGradient(colors: [Color.silver.opacity(0.3), Color.seat], startPoint: .topLeading, endPoint: .bottomTrailing)
                }
            }
            .overlay {
                LinearGradient(colors: [Color.room.opacity(0.92), Color.room.opacity(0.1)], startPoint: .bottom, endPoint: .center)
            }
            .overlay(alignment: .bottomLeading) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(genre.name)
                        .font(.marquee(20, weight: .heavy))
                        .foregroundStyle(Color.screen)
                    Text(caption)
                        .font(.caption.weight(.medium))
                        .foregroundStyle(Color.usher)
                }
                .lineLimit(1)
                .padding(10)
            }
            .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}
