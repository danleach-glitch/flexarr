import FlexarrKit
import SwiftUI

/// An audiobooks library's books in a grid: All books on the library's tab, under its switch; a shelf's See all
/// (Recently added, Finished) under the library's name; or one genre's books under its name, Back going to the genres.
/// The iPhone's seven sorts and its filters (progress, length, unabridged) are two menus at the top right, and sorted
/// A to Z an A–Z rail runs down the right, as on a film library's grid.
struct BookGridView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.focusesContent) private var arriving
    let library: Library
    var genre: String?
    /// On the library's tab, the Browse, All books, Genres, Series and Authors switch.
    var view: Binding<BookOpening>?
    @State private var filter: BookFilter
    @State private var books: [ItemSummary] = []
    @State private var total = 0
    @State private var loaded = false
    @State private var loadedToken: Int?
    @State private var loadingMore = false
    @State private var error: String?
    /// Bumped by each new sort or filter, so an answer for the old one is dropped.
    @State private var generation = 0
    /// The letter being jumped to while the books up to it load.
    @State private var jumping: String?
    @FocusState private var focused: String?
    /// The menus and the A–Z rail.
    @FocusState private var focusedControl: String?

    private static let pageSize = 60
    /// The most the server sends in one page.
    private static let maxPageSize = 500
    /// Room on the right for the A–Z rail; the six covers close up a little to make it (as `LibraryView`'s).
    private static let railGutter: CGFloat = 170

    init(library: Library, genre: String? = nil, view: Binding<BookOpening>? = nil, initialSort: BookSort = .title,
         initialProgress: BookProgressFilter = .any) {
        self.library = library
        self.genre = genre
        self.view = view
        _filter = State(initialValue: BookFilter(progress: initialProgress, genre: genre, sort: initialSort))
    }

    /// The rail only makes sense when the books are A to Z.
    private var showsLetters: Bool {
        filter.sort == .title && (filter.descending ?? false) == false && total > Self.pageSize
    }

    private var columns: [GridItem] {
        let trailing = showsLetters ? Self.railGutter : Layout.edge
        let spacing = (1920 - Layout.edge - trailing - 6 * Layout.posterWidth) / 5
        return Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: spacing, alignment: .top), count: 6)
    }

    private var isFiltered: Bool {
        filter.progress != .any || filter.abridged != nil || filter.minHours != nil || filter.maxHours != nil
    }

    var body: some View {
        ScrollViewReader { proxy in
            grid
                .overlay(alignment: .trailing) {
                    if showsLetters {
                        LetterRail(letters: LibraryView.letters, empty: emptyLetters, jumping: jumping, focus: $focusedControl) { letter in
                            Task { await jump(to: letter, proxy: proxy) }
                        }
                        .padding(.trailing, Layout.edge)
                        .padding(.top, view != nil ? 60 : 0)
                    }
                }
        }
    }

    private var grid: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 40) {
                header
                if loaded, books.isEmpty {
                    if isFiltered { noMatches } else { StatusView(state: .empty(t("No books yet."))).frame(height: 500) }
                } else if loaded {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                        ForEach(Array(books.enumerated()), id: \.element.id) { index, book in
                            BookCard(item: book, focus: $focused) { router.push(.item(book)) }
                                .id(book.id)
                                .onAppear { if index >= books.count - 12 { Task { await more() } } }
                        }
                    }
                    .focusSection()
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 500)
                } else {
                    StatusView(state: .loading).frame(height: 500)
                }
            }
            .padding(.leading, Layout.edge)
            .padding(.trailing, showsLetters ? Self.railGutter : Layout.edge)
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .ignoresSafeArea(edges: view == nil ? .horizontal : [.top, .horizontal])
        .background(RoomBackground(ambient: books.first?.glowColor, intensity: 0.2))
        // Once, and again when something changed (a book marked finished): not on every return from a book, which would
        // drop the pages scrolled through and the focus with them.
        .task(id: model.refreshToken) {
            guard loadedToken != model.refreshToken else { return }
            await load()
        }
        .onChange(of: filter) {
            books = []
            loaded = false
            Task { await load() }
        }
        .onChange(of: arriving.wrappedValue, focusFirstBookOnArrival)
        .onChange(of: books.first?.id, focusFirstBookOnArrival)
    }

    private var header: some View {
        HStack(alignment: .lastTextBaseline, spacing: 30) {
            BookPageHeading(view: view, title: genre ?? library.name, count: loaded ? summary : nil)
            Spacer()
            HStack(spacing: 18) {
                sortMenu
                filterMenu
            }
            .focusSection()
        }
        .padding(.top, view == nil ? 20 : LibraryViewSwitch.topInset)
    }

    /// "42 books", with what they're narrowed to.
    private var summary: String {
        var parts = [t("{count, plural, one {# book} other {# books}}", ["count": total])]
        if filter.progress != .any { parts.append(filter.progress.label.lowercased()) }
        let length = BookLength(filter)
        if length != .any { parts.append(length.label.lowercased()) }
        if filter.abridged == false { parts.append("unabridged") }
        return parts.joined(separator: ", ")
    }

    /// The seven sorts, in the iPhone's order, and Reverse.
    private var sortMenu: some View {
        Menu {
            Section(t("Sort by")) {
                ForEach(BookSort.allCases) { sort in
                    Button {
                        filter.sort = sort
                        filter.descending = nil
                    } label: {
                        if filter.sort == sort { Label(sort.label, systemImage: "checkmark") } else { Text(sort.label) }
                    }
                }
            }
            Button {
                filter.descending = !(filter.descending ?? filter.sort.descendingByDefault)
            } label: {
                if isReversed { Label(t("Reverse"), systemImage: "checkmark") } else { Text(t("Reverse")) }
            }
        } label: {
            Label(filter.sort.label, systemImage: "arrow.up.arrow.down")
        }
        .focused($focusedControl, equals: "sort")
        .buttonStyle(PillButtonStyle(selected: false))
        .accessibilityLabel(t("Sort by {order}", ["order": filter.sort.label]))
    }

    private var isReversed: Bool {
        (filter.descending ?? filter.sort.descendingByDefault) != filter.sort.descendingByDefault
    }

    /// Progress, length and unabridged, as the iPhone's filter menu has them.
    private var filterMenu: some View {
        Menu {
            Section(t("Show")) {
                ForEach(BookProgressFilter.allCases) { progress in
                    Button { filter.progress = progress } label: {
                        if filter.progress == progress { Label(progress.label, systemImage: "checkmark") } else { Text(progress.label) }
                    }
                }
            }
            Section(t(Words.length)) {
                ForEach(BookLength.allCases) { length in
                    Button { length.apply(to: &filter) } label: {
                        if BookLength(filter) == length { Label(length.label, systemImage: "checkmark") } else { Text(length.label) }
                    }
                }
            }
            Section {
                Button { filter.abridged = filter.abridged == false ? nil : false } label: {
                    if filter.abridged == false { Label(t("Unabridged only"), systemImage: "checkmark") } else { Text(t("Unabridged only")) }
                }
                if isFiltered {
                    Button(t("Clear filters"), action: clearFilters)
                }
            }
        } label: {
            Label(t("Filter"), systemImage: isFiltered ? "line.3.horizontal.decrease.circle.fill" : "line.3.horizontal.decrease.circle")
        }
        .focused($focusedControl, equals: "filter")
        .buttonStyle(PillButtonStyle(selected: isFiltered))
    }

    /// Filters that leave nothing: say so, with a way back to every book (the sort, and a genre's page its genre, stay).
    private var noMatches: some View {
        VStack(spacing: 30) {
            Text(t("No books match."))
                .font(.system(size: 32, weight: .medium))
                .foregroundStyle(Color.usher)
            Button(t("Clear filters"), action: clearFilters)
                .buttonStyle(ActionButtonStyle(prominent: true))
        }
        .frame(maxWidth: .infinity)
        .frame(height: 500)
        .focusSection()
    }

    private func clearFilters() {
        filter = BookFilter(genre: genre, sort: filter.sort, descending: filter.descending)
    }

    /// From the switch, as the tab opens (see `focusesContent`): to the first book, once it's loaded.
    private func focusFirstBookOnArrival() {
        guard view != nil, arriving.wrappedValue, let first = books.first else { return }
        arriving.wrappedValue = false
        focused = first.id
    }

    private func load() async {
        guard let client = model.client else { return }
        generation += 1
        let current = generation
        let token = model.refreshToken
        do {
            // As many as were showing, so a refresh keeps the place.
            let page = try await client.books(library.id, filter: filter, take: min(200, max(Self.pageSize, books.count)))
            guard current == generation else { return }
            books = page.items
            total = page.total
            loaded = true
            error = nil
            loadedToken = token
        } catch {
            if current == generation, !loaded { self.error = ConnectionError(error).message }
        }
    }

    private func more() async {
        guard let client = model.client, !loadingMore, jumping == nil, loaded, books.count < total else { return }
        let current = generation
        let skip = books.count
        loadingMore = true
        defer { loadingMore = false }
        if let page = try? await client.books(library.id, filter: filter, skip: skip, take: Self.pageSize), current == generation, skip == books.count {
            let known = Set(books.map(\.id))
            books += page.items.filter { !known.contains($0.id) }
            total = page.total
        }
    }

    /// Letters with nothing to jump to (see `LibraryView`'s).
    private var emptyLetters: Set<String> {
        let found = Set(books.map(LibraryView.letter(of:)))
        let complete = books.count >= total
        let last = books.last.map(LibraryView.letter(of:)) ?? "#"
        return Set(LibraryView.letters.filter { letter in
            !found.contains(letter) && (complete || (letter != "#" && last != "#" && last > letter))
        })
    }

    /// Loads books until the first one under `letter` is in the grid, then scrolls to it and gives it focus.
    private func jump(to letter: String, proxy: ScrollViewProxy) async {
        guard let client = model.client, jumping == nil else { return }
        let current = generation
        jumping = letter
        defer { jumping = nil }
        while current == generation {
            if let target = books.first(where: { LibraryView.letter(of: $0) == letter }) {
                // Books just added aren't laid out yet; give the grid a moment, then once more for focus.
                await Task.yield()
                proxy.scrollTo(target.id, anchor: .top)
                try? await Task.sleep(for: .milliseconds(150))
                proxy.scrollTo(target.id, anchor: .top)
                focused = target.id
                return
            }
            guard !emptyLetters.contains(letter), books.count < total else { return }
            let skip = books.count
            guard let page = try? await client.books(library.id, filter: filter, skip: skip, take: Self.maxPageSize),
                  current == generation, !page.items.isEmpty else { return }
            guard skip == books.count else { continue }
            let known = Set(books.map(\.id))
            books.append(contentsOf: page.items.filter { !known.contains($0.id) })
            total = page.total
        }
    }
}
