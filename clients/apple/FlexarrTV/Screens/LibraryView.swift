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

    private static let pageSize = 60
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
            // Coming back from a detail screen runs this again. Keep what's loaded, and so the scroll
            // position, only bringing it up to date if something changed meanwhile.
            if let loadedToken, !items.isEmpty {
                if loadedToken != model.refreshToken { await refreshLoaded() }
                return
            }
            async let genresTask = try? model.client?.libraryGenres(library.id)
            await reload()
            genres = await genresTask ?? []
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
        isLoading = true
        defer { isLoading = false }
        let token = model.refreshToken
        do {
            let page = try await client.libraryItems(library.id, sort: sort, genre: genre, skip: 0, take: Self.pageSize)
            items = page.items
            total = page.total
            loadedToken = token
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    /// After playback or watched changes, refetch what is already on screen so badges update.
    private func refreshLoaded() async {
        guard let client = model.client, !items.isEmpty else { return }
        let token = model.refreshToken
        if let page = try? await client.libraryItems(library.id, sort: sort, genre: genre, skip: 0, take: max(items.count, Self.pageSize)) {
            items = page.items
            total = page.total
            loadedToken = token
        }
    }

    private func loadMoreIfNeeded(at index: Int) {
        guard !isLoading, items.count < total, index >= items.count - 18 else { return }
        Task {
            guard let client = model.client else { return }
            isLoading = true
            defer { isLoading = false }
            if let page = try? await client.libraryItems(library.id, sort: sort, genre: genre, skip: items.count, take: Self.pageSize) {
                let known = Set(items.map(\.id))
                items.append(contentsOf: page.items.filter { !known.contains($0.id) })
                total = page.total
            }
        }
    }
}
