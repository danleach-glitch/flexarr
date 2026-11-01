import FlexarrKit
import SwiftUI

/// What an audiobooks library shows: its shelves, or every book, series, author or narrator (named as on the web).
enum BookBrowse: String, CaseIterable, Identifiable {
    case shelves = "Browse", all = "All books", series = "Series", authors = "Authors", narrators = "Narrators"
    var id: String { rawValue }
}

/// An audiobooks library's state: the shelves, and a page of each list, loaded as they're scrolled.
@MainActor
@Observable
final class BooksLibraryModel {
    let library: Library
    var browse: BookBrowse = .shelves
    var filter = BookFilter()
    private(set) var shelves: BookShelves?
    private(set) var books: [ItemSummary] = []
    private(set) var booksTotal = 0
    private(set) var series: [BookSeriesSummary] = []
    private(set) var seriesTotal = 0
    private(set) var people: [BookPerson] = []
    private(set) var peopleTotal = 0
    private(set) var isLoading = false
    private(set) var error: String?
    private(set) var loadedAt: Date?

    init(library: Library) {
        self.library = library
    }

    func load(client: FlexarrClient) async {
        isLoading = true
        defer { isLoading = false }
        do {
            switch browse {
            case .shelves:
                shelves = try await client.bookShelves(library.id)
            case .all:
                let page = try await client.books(library.id, filter: filter, take: 60)
                books = page.items
                booksTotal = page.total
            case .series:
                let page = try await client.bookSeriesList(library.id, take: 60)
                series = page.items
                seriesTotal = page.total
            case .authors, .narrators:
                let page = try await client.bookPeople(library.id, role: browse == .authors ? .author : .narrator, take: 90)
                people = page.items
                peopleTotal = page.total
            }
            error = nil
            loadedAt = Date()
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    /// The next page, once the list is scrolled near its end.
    func loadMoreIfNeeded(at index: Int, client: FlexarrClient) {
        guard !isLoading else { return }
        switch browse {
        case .all where index >= books.count - 10 && books.count < booksTotal:
            more { [library, filter, books] in
                let page = try await client.books(library.id, filter: filter, skip: books.count, take: 60)
                self.books += page.items
            }
        case .series where index >= series.count - 10 && series.count < seriesTotal:
            more { [library, series] in
                let page = try await client.bookSeriesList(library.id, skip: series.count, take: 60)
                self.series += page.items
            }
        case .authors, .narrators:
            guard index >= people.count - 10, people.count < peopleTotal else { return }
            let role: BookPersonRole = browse == .authors ? .author : .narrator
            more { [library, people] in
                let page = try await client.bookPeople(library.id, role: role, skip: people.count, take: 90)
                self.people += page.items
            }
        default:
            break
        }
    }

    private func more(_ work: @escaping @MainActor () async throws -> Void) {
        isLoading = true
        Task {
            try? await work()
            isLoading = false
        }
    }
}

/// An audiobooks library: shelves to start (Continue listening, Next in series, new books, series, authors,
/// narrators, genres, finished), and the whole library as books, series, authors or narrators.
struct BooksLibraryView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.scenePhase) private var scenePhase
    @State private var browse: BooksLibraryModel
    @State private var isVisible = false

    init(library: Library) {
        _browse = State(initialValue: BooksLibraryModel(library: library))
    }

    private var columns: [GridItem] {
        let minimum: CGFloat = sizeClass == .regular ? 150 : 104
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.45), spacing: 12, alignment: .top)]
    }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 24) {
                tabs
                if let error = browse.error, isEmpty {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 400)
                } else if isEmpty, !browse.isLoading, browse.loadedAt != nil {
                    if browse.browse == .all, isFiltered {
                        noMatches.frame(height: 400)
                    } else {
                        StatusView(state: .empty("No books yet.")).frame(height: 400)
                    }
                } else {
                    content
                }
                if browse.isLoading {
                    ProgressView().frame(maxWidth: .infinity).padding(20)
                }
            }
            .padding(.top, 8)
            .padding(.bottom, 24)
        }
        .background(RoomBackground(ambient: browse.shelves?.continueListening.first?.glowColor, intensity: 0.18))
        .navigationTitle(browse.library.name)
        .toolbar {
            if browse.browse == .all {
                ToolbarItem(placement: .topBarTrailing) { filterMenu }
            }
        }
        .refreshable { await load() }
        .task(id: "\(browse.browse.rawValue)#\(browse.filter.hashValue)") { await load() }
        .task(id: model.refreshToken) { if browse.loadedAt != nil { await load() } }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible, Freshness.isStale(browse.loadedAt) { Task { await load() } }
        }
    }

    private var isEmpty: Bool {
        switch browse.browse {
        case .shelves: browse.shelves?.isEmpty ?? true
        case .all: browse.books.isEmpty
        case .series: browse.series.isEmpty
        case .authors, .narrators: browse.people.isEmpty
        }
    }

    private var tabs: some View {
        ScrollView(.horizontal) {
            HStack(spacing: 8) {
                ForEach(BookBrowse.allCases) { tab in
                    Button(tab.rawValue) { browse.browse = tab }
                        .buttonStyle(PillButtonStyle(selected: browse.browse == tab))
                }
            }
            .padding(.horizontal, Layout.edge)
        }
        .scrollIndicators(.hidden)
    }

    @ViewBuilder
    private var content: some View {
        switch browse.browse {
        case .shelves: shelves
        case .all: allBooks
        case .series: seriesGrid
        case .authors, .narrators: peopleGrid
        }
    }

    // MARK: Shelves

    @ViewBuilder
    private var shelves: some View {
        if let shelves = browse.shelves {
            bookRow("Continue listening", shelves.continueListening)
            bookRow("Next in series", shelves.nextInSeries)
            bookRow("Recently added", shelves.recentlyAdded) { show(.all, sort: .added) }
            if !shelves.series.isEmpty {
                row("Series", seeAll: { browse.browse = .series }) {
                    ForEach(shelves.series) { SeriesCard(series: $0, width: cardWidth) }
                }
            }
            peopleRow("Authors", shelves.authors) { browse.browse = .authors }
            if !shelves.genres.isEmpty {
                VStack(alignment: .leading, spacing: 10) {
                    SectionHeading(title: "Genres")
                    ScrollView(.horizontal) {
                        HStack(spacing: 8) {
                            ForEach(shelves.genres, id: \.name) { genre in
                                Button(genre.name) {
                                    browse.filter = BookFilter(genre: genre.name)
                                    browse.browse = .all
                                }
                                .buttonStyle(PillButtonStyle(selected: false))
                            }
                        }
                        .padding(.horizontal, Layout.edge)
                    }
                    .scrollIndicators(.hidden)
                }
            }
            bookRow("Finished", shelves.finished) { show(.all, progress: .finished) }
        }
    }

    private var cardWidth: CGFloat { Layout.posterWidth(sizeClass) * 1.15 }

    @ViewBuilder
    private func bookRow(_ title: String, _ items: [ItemSummary], seeAll: (() -> Void)? = nil) -> some View {
        if !items.isEmpty {
            row(title, seeAll: seeAll) {
                ForEach(items) { BookCard(item: $0, width: cardWidth) }
            }
        }
    }

    @ViewBuilder
    private func peopleRow(_ title: String, _ people: [BookPerson], seeAll: @escaping () -> Void) -> some View {
        if !people.isEmpty {
            row(title, seeAll: seeAll) {
                ForEach(people) { person in
                    PersonCard(id: person.id, name: person.name, detail: bookCount(person.bookCount), image: person.image)
                }
            }
        }
    }

    private func row(_ title: String, seeAll: (() -> Void)?, @ViewBuilder content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: title, action: seeAll)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) { content() }
                    .padding(.horizontal, Layout.edge)
            }
            .scrollIndicators(.hidden)
        }
    }

    private func show(_ tab: BookBrowse, sort: BookSort = .title, progress: BookProgressFilter = .any) {
        browse.filter = BookFilter(progress: progress, sort: sort)
        browse.browse = tab
    }

    private func bookCount(_ count: Int) -> String { count == 1 ? "1 book" : "\(count) books" }

    // MARK: Lists

    private var allBooks: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(summary)
                .font(.subheadline.weight(.medium))
                .foregroundStyle(Color.usher)
                .monospacedDigit()
                .padding(.horizontal, Layout.edge)
            LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                ForEach(Array(browse.books.enumerated()), id: \.element.id) { index, book in
                    BookCard(item: book)
                        .onAppear { if let client = model.client { browse.loadMoreIfNeeded(at: index, client: client) } }
                }
            }
            .padding(.horizontal, Layout.edge)
        }
    }

    /// "42 books", with what they're narrowed to.
    private var summary: String {
        var parts = [bookCount(browse.booksTotal)]
        if browse.filter.progress != .any { parts.append(browse.filter.progress.label.lowercased()) }
        if let genre = browse.filter.genre { parts.append(genre) }
        return parts.joined(separator: ", ")
    }

    private var seriesGrid: some View {
        LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
            ForEach(Array(browse.series.enumerated()), id: \.element.id) { index, series in
                SeriesCard(series: series)
                    .onAppear { if let client = model.client { browse.loadMoreIfNeeded(at: index, client: client) } }
            }
        }
        .padding(.horizontal, Layout.edge)
    }

    private var peopleGrid: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 96), spacing: 8, alignment: .top)], alignment: .center, spacing: 20) {
            ForEach(Array(browse.people.enumerated()), id: \.element.id) { index, person in
                PersonCard(id: person.id, name: person.name, detail: bookCount(person.bookCount), image: person.image)
                    .onAppear { if let client = model.client { browse.loadMoreIfNeeded(at: index, client: client) } }
            }
        }
        .padding(.horizontal, Layout.edge)
    }

    // MARK: Filters and sorting

    private var filterMenu: some View {
        Menu {
            Picker("Show", selection: $browse.filter.progress) {
                ForEach(BookProgressFilter.allCases) { Text($0.label).tag($0) }
            }
            Picker("Length", selection: Binding(get: { BookLength(browse.filter) }, set: { $0.apply(to: &browse.filter) })) {
                ForEach(BookLength.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.menu)
            Toggle("Unabridged only", isOn: Binding(get: { browse.filter.abridged == false }, set: { browse.filter.abridged = $0 ? false : nil }))
            if browse.filter.genre != nil {
                Button("All genres") { browse.filter.genre = nil }
            }
            Section("Sort by") {
                Picker("Sort by", selection: Binding(get: { browse.filter.sort }, set: { browse.filter.sort = $0; browse.filter.descending = nil })) {
                    ForEach(BookSort.allCases) { Text($0.label).tag($0) }
                }
                Toggle("Reverse", isOn: Binding(get: { (browse.filter.descending ?? browse.filter.sort.descendingByDefault) != browse.filter.sort.descendingByDefault },
                                                set: { browse.filter.descending = $0 != browse.filter.sort.descendingByDefault }))
            }
        } label: {
            Image(systemName: isFiltered ? "line.3.horizontal.decrease.circle.fill" : "line.3.horizontal.decrease.circle")
        }
        .accessibilityLabel("Filter and sort")
    }

    /// Filters that leave nothing: say so, with a way back to every book (the sort stays).
    private var noMatches: some View {
        VStack(spacing: 16) {
            Text("No books match.")
                .font(.body.weight(.medium))
                .foregroundStyle(Color.usher)
            Button("Clear filters") { browse.filter = BookFilter(sort: browse.filter.sort, descending: browse.filter.descending) }
                .buttonStyle(ActionButtonStyle(prominent: true))
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var isFiltered: Bool {
        browse.filter.progress != .any || browse.filter.genre != nil || browse.filter.abridged != nil || browse.filter.minHours != nil || browse.filter.maxHours != nil
    }

    private func load() async {
        guard let client = model.client else { return }
        await browse.load(client: client)
    }
}

/// The lengths the filter offers.
private enum BookLength: String, CaseIterable, Identifiable {
    case any, short, medium, long, epic
    var id: String { rawValue }

    var label: String {
        switch self {
        case .any: "Any length"
        case .short: "Under 5 hours"
        case .medium: "5 to 10 hours"
        case .long: "10 to 20 hours"
        case .epic: "Over 20 hours"
        }
    }

    var hours: (Double?, Double?) {
        switch self {
        case .any: (nil, nil)
        case .short: (nil, 5)
        case .medium: (5, 10)
        case .long: (10, 20)
        case .epic: (20, nil)
        }
    }

    init(_ filter: BookFilter) {
        self = Self.allCases.first { $0.hours.0 == filter.minHours && $0.hours.1 == filter.maxHours } ?? .any
    }

    func apply(to filter: inout BookFilter) {
        (filter.minHours, filter.maxHours) = hours
    }
}
