import FlexarrKit
import SwiftUI

/// Poster grid for one library, paged with skip/take, with sort and genre filters.
struct LibraryView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let library: Library
    var initialSort: LibrarySort = .title

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

    private static let pageSize = 60
    /// The most the server sends in one page (ItemEndpoints' MaxPageSize).
    private static let maxPageSize = 500
    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 30) {
                header
                if let error, items.isEmpty {
                    StatusView(state: .failed(error) { Task { await reload() } })
                        .frame(height: 500)
                } else if items.isEmpty && !isLoading {
                    StatusView(state: .empty(genre == nil ? "Nothing in this library yet." : "No \(genre!) titles here."))
                        .frame(height: 500)
                } else {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 64) {
                        ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                            PosterCard(item: item) { router.push(.item(item)) }
                                .onAppear { loadMoreIfNeeded(at: index) }
                        }
                    }
                    .focusSection()
                    if isLoading {
                        ProgressView().frame(maxWidth: .infinity).padding(40)
                    }
                }
            }
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .background(RoomBackground(ambient: items.first?.glowColor, intensity: 0.18))
        .task {
            if !didApplyInitialSort {
                didApplyInitialSort = true
                sort = initialSort
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

    private var header: some View {
        HStack(alignment: .lastTextBaseline, spacing: 30) {
            VStack(alignment: .leading, spacing: 4) {
                Text(library.name)
                    .font(.marquee(76, weight: .heavy))
                    .foregroundStyle(Color.screen)
                Text(total == 1 ? "1 title" : "\(total.formatted()) titles")
                    .font(.system(size: 24, weight: .medium))
                    .foregroundStyle(Color.usher)
                    .monospacedDigit()
            }
            Spacer()
            HStack(spacing: 18) {
                ForEach(LibrarySort.allCases) { option in
                    Button(option.label) { sort = option }
                        .buttonStyle(PillButtonStyle(selected: sort == option))
                }
                if !genres.isEmpty {
                    Menu {
                        Button("All genres") { genre = nil }
                        ForEach(genres, id: \.name) { g in
                            Button("\(g.name)  (\(g.count))") { genre = g.name }
                        }
                    } label: {
                        Label(genre ?? "All genres", systemImage: "line.3.horizontal.decrease")
                    }
                    .buttonStyle(PillButtonStyle(selected: genre != nil))
                }
            }
            .focusSection()
        }
        .padding(.top, 20)
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
    /// Only one runs at a time (playback ending and Back both ask within a second or two); one asked for while
    /// it runs because something changed again is run once it finishes.
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
    /// (a page failed, or more were appended meanwhile) is kept as it was, so focus and scroll stay put.
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
