import FlexarrKit
import SwiftUI

/// Which of an audiobooks library's views its tab opens on, in the iPhone's order: Browse (a hero and the shelves),
/// All books, Genres, Series or Authors. Settings › Libraries, for every audiobooks library on this Apple TV.
enum BookOpening: String, CaseIterable {
    case browse
    case all
    case genres
    case series
    case authors

    static let storageKey = "FlexarrBooksOpenOn"

    var label: String {
        switch self {
        case .browse: t(Words.browse)
        case .all: t("All books")
        case .genres: t(Words.genres)
        case .series: t(Words.series)
        case .authors: t("Authors")
        }
    }

    /// Before books had a choice of their own they opened as films and shows did (`LibraryOpening`, whose Browse, All
    /// and Genres are these): that choice holds until one is made here.
    static func opening(saved: BookOpening?, library: LibraryOpening) -> BookOpening {
        saved ?? BookOpening(rawValue: library.rawValue) ?? .browse
    }
}

/// An audiobooks library's tab: Browse, All books, Genres, Series and Authors, switched with the words at the top, as
/// a film library's are. A shelf's See all opens its books, series or authors on top instead, so Back comes back to
/// the shelves.
struct BooksLibraryTab: View {
    let library: Library
    @AppStorage(BookOpening.storageKey) private var opensOn: BookOpening?
    @AppStorage(LibraryOpening.storageKey) private var libraryOpensOn = LibraryOpening.browse
    /// Picked with the switch; until then (and again after Settings changes), the setting.
    @State private var picked: BookOpening?
    /// Just switched: the view taking over puts focus on its switch once (see `LibraryTab`).
    @State private var switching = false
    /// Focus has just come to the switch with the tab (see `focusesContent`).
    @State private var arriving = false

    var body: some View {
        let saved = BookOpening.opening(saved: opensOn, library: libraryOpensOn)
        let view = Binding { picked ?? saved } set: {
            switching = $0 != (picked ?? saved)
            picked = $0
        }
        Group {
            switch view.wrappedValue {
            case .browse: BooksLibraryView(library: library, view: view)
            case .all: BookGridView(library: library, view: view)
            case .genres: LibraryGenresView(library: library, bookView: view)
            case .series: BookSeriesGridView(library: library, view: view)
            case .authors: BookAuthorsGridView(library: library, view: view)
            }
        }
        .environment(\.focusesViewSwitch, $switching)
        .environment(\.focusesContent, $arriving)
        .onChange(of: opensOn) { picked = nil }
        .onChange(of: libraryOpensOn) { picked = nil }
    }
}

/// "Browse", "All books", "Genres", "Series" and "Authors" as quiet words at the top-left (see `LibraryViewSwitch`).
struct BookViewSwitch: View {
    @Binding var view: BookOpening
    var focusChanged: (Bool) -> Void = { _ in }

    var body: some View {
        ViewSwitch(choices: BookOpening.allCases.map { ($0, $0.label) }, selection: $view, focusChanged: focusChanged)
    }
}

/// The heading of an audiobooks library's pages: the switch on the library's tab, else the page's name, with what
/// it holds beneath.
struct BookPageHeading: View {
    var view: Binding<BookOpening>?
    let title: String
    var count: String?

    var body: some View {
        VStack(alignment: .leading, spacing: view == nil ? 4 : 16) {
            if let view {
                BookViewSwitch(view: view).focusSection()
            } else {
                Text(title)
                    .font(.marquee(76, weight: .heavy))
                    .foregroundStyle(Color.screen)
            }
            if let count {
                Text(count)
                    .font(.system(size: 24, weight: .medium))
                    .foregroundStyle(Color.usher)
                    .monospacedDigit()
            }
        }
    }
}

/// Every series in an audiobooks library, A to Z: each opens its books in reading order.
struct BookSeriesGridView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.focusesContent) private var arriving
    let library: Library
    var view: Binding<BookOpening>?
    @State private var series: [BookSeriesSummary] = []
    @State private var total = 0
    @State private var loaded = false
    @State private var loadedToken: Int?
    @State private var loadingMore = false
    @State private var error: String?
    @FocusState private var focused: String?

    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 40) {
                BookPageHeading(view: view, title: t(Words.series), count: loaded ? (t("{count, plural, one {# series} other {# series}}", ["count": total])) : nil)
                    .padding(.top, view == nil ? 20 : LibraryViewSwitch.topInset)
                if loaded, series.isEmpty {
                    StatusView(state: .empty(t("No series yet."))).frame(height: 500)
                } else if loaded {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                        ForEach(Array(series.enumerated()), id: \.element.id) { index, one in
                            SeriesCard(series: one, focus: $focused) { router.push(.bookSeries(id: one.id, name: one.name)) }
                                .onAppear { if index >= series.count - 12 { Task { await more() } } }
                        }
                    }
                    .focusSection()
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 500)
                } else {
                    StatusView(state: .loading).frame(height: 500)
                }
            }
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .ignoresSafeArea(edges: view == nil ? .horizontal : [.top, .horizontal])
        .background(RoomBackground(ambient: nil, intensity: 0.2))
        .task(id: model.refreshToken) {
            guard loadedToken != model.refreshToken else { return }
            await load()
        }
        .onChange(of: arriving.wrappedValue, focusFirstOnArrival)
        .onChange(of: series.first?.id, focusFirstOnArrival)
    }

    private func focusFirstOnArrival() {
        guard view != nil, arriving.wrappedValue, let first = series.first else { return }
        arriving.wrappedValue = false
        focused = first.id
    }

    private func load() async {
        guard let client = model.client else { return }
        let token = model.refreshToken
        do {
            let page = try await client.bookSeriesList(library.id, take: min(200, max(60, series.count)))
            series = page.items
            total = page.total
            loaded = true
            error = nil
            loadedToken = token
        } catch {
            if !loaded { self.error = ConnectionError(error).message }
        }
    }

    private func more() async {
        guard let client = model.client, !loadingMore, loaded, series.count < total else { return }
        let skip = series.count
        loadingMore = true
        defer { loadingMore = false }
        if let page = try? await client.bookSeriesList(library.id, skip: skip, take: 60), skip == series.count {
            let known = Set(series.map(\.id))
            series += page.items.filter { !known.contains($0.id) }
            total = page.total
        }
    }
}

/// Every author in an audiobooks library, A to Z: each opens their page.
struct BookAuthorsGridView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.focusesContent) private var arriving
    let library: Library
    var view: Binding<BookOpening>?
    @State private var people: [BookPerson] = []
    @State private var total = 0
    @State private var loaded = false
    @State private var loadedToken: Int?
    @State private var loadingMore = false
    @State private var error: String?
    @FocusState private var focused: String?

    /// The people cards' width (180) and room for their names.
    private let columns = Array(repeating: GridItem(.fixed(220), spacing: 36, alignment: .top), count: 7)

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 40) {
                BookPageHeading(view: view, title: t("Authors"), count: loaded ? (t("{count, plural, one {# author} other {# authors}}", ["count": total])) : nil)
                    .padding(.top, view == nil ? 20 : LibraryViewSwitch.topInset)
                if loaded, people.isEmpty {
                    StatusView(state: .empty(t("No authors yet."))).frame(height: 500)
                } else if loaded {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                        ForEach(Array(people.enumerated()), id: \.element.id) { index, person in
                            PersonCard(name: person.name, detail: t("{count, plural, one {# book} other {# books}}", ["count": person.bookCount]), image: person.image,
                                       focus: $focused, focusID: person.id) {
                                router.push(.person(id: person.id, name: person.name))
                            }
                            .onAppear { if index >= people.count - 14 { Task { await more() } } }
                        }
                    }
                    .focusSection()
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 500)
                } else {
                    StatusView(state: .loading).frame(height: 500)
                }
            }
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .ignoresSafeArea(edges: view == nil ? .horizontal : [.top, .horizontal])
        .background(RoomBackground(ambient: nil, intensity: 0.2))
        .task(id: model.refreshToken) {
            guard loadedToken != model.refreshToken else { return }
            await load()
        }
        .onChange(of: arriving.wrappedValue, focusFirstOnArrival)
        .onChange(of: people.first?.id, focusFirstOnArrival)
    }

    private func focusFirstOnArrival() {
        guard view != nil, arriving.wrappedValue, let first = people.first else { return }
        arriving.wrappedValue = false
        focused = first.id
    }

    private func load() async {
        guard let client = model.client else { return }
        let token = model.refreshToken
        do {
            let page = try await client.bookPeople(library.id, role: .author, take: min(210, max(84, people.count)))
            people = page.items
            total = page.total
            loaded = true
            error = nil
            loadedToken = token
        } catch {
            if !loaded { self.error = ConnectionError(error).message }
        }
    }

    private func more() async {
        guard let client = model.client, !loadingMore, loaded, people.count < total else { return }
        let skip = people.count
        loadingMore = true
        defer { loadingMore = false }
        if let page = try? await client.bookPeople(library.id, role: .author, skip: skip, take: 84), skip == people.count {
            let known = Set(people.map(\.id))
            people += page.items.filter { !known.contains($0.id) }
            total = page.total
        }
    }
}
