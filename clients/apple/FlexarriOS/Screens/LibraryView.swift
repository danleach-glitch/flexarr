import FlexarrKit
import SwiftUI

/// One library: Browse (a hero and rows of just this library), All (a poster grid paged with skip/take, with sort and
/// genre in the toolbar) and, for films and shows, Genres (a tile for each, opening its titles in that genre). The
/// plain library opens on Browse; one asked for with a sort or genre (a row's See all) opens on its titles. A music
/// library has Browse, Artists, Albums and Genres; its genre's page is the genre's albums, with Play and Shuffle.
struct LibraryView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let library: Library
    var initialSort: LibrarySort = .title
    /// Opened from a genre's tile: just its titles, under its name.
    var initialGenre: String?

    /// `all` is a music library's artists; `albums` is only for music.
    enum Showing: Hashable { case browse, all, albums, genres }
    @State private var showing: Showing
    /// The views opened so far. Each stays in place, hidden, when another is picked, so switching back finds it
    /// where it was left rather than loading it again.
    @State private var opened: Set<Showing>

    @State private var sort: LibrarySort = .title
    @State private var genre: String?
    /// The Mac's search of this library (its toolbar field): All, narrowed to titles with these words.
    @State private var search = ""
    /// The search the grid last loaded for, so arriving on the page doesn't load it twice.
    @State private var searchedFor = ""
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
    /// A music library's genres counted by album, for its Albums filter and Genres tiles (`genres` are its artists').
    @State private var albumGenres: [GenreCount] = []
    /// What the grid holds or is loading: a music library's albums (else its artists, or a library's titles).
    @State private var gridHoldsAlbums: Bool?
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.horizontalSizeClass) private var sizeClass

    private static let pageSize = 60
    /// The most the server sends in one page (ItemEndpoints' MaxPageSize).
    private static let maxPageSize = 500

    /// `opening`: the view to open on, when a link says (a music library's Albums or Artists).
    init(library: Library, initialSort: LibrarySort = .title, initialGenre: String? = nil, opening: Showing? = nil) {
        self.library = library
        self.initialSort = initialSort
        self.initialGenre = initialGenre
        let first: Showing = opening ?? (library.type == .music && initialGenre != nil ? .albums
            : Self.hasBrowse(library, genre: initialGenre) && initialSort == .title && initialGenre == nil ? .browse : .all)
        _showing = State(initialValue: first)
        _opened = State(initialValue: [first])
    }

    private var columns: [GridItem] {
        let minimum: CGFloat = sizeClass == .regular ? 140 : 100
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.45), spacing: 12, alignment: .top)]
    }

    /// Films, shows and home videos have a Browse page; a genre's own page doesn't offer it again.
    private static func hasBrowse(_ library: Library, genre: String?) -> Bool {
        genre == nil && [.movies, .tvShows, .homeVideos, .music].contains(library.type)
    }

    /// Films, shows and music have genres to browse by; a genre's own page doesn't offer them again.
    private var hasGenres: Bool {
        initialGenre == nil && (library.type == .movies || library.type == .tvShows || (library.type == .music && offersAlbums))
    }

    private var music: Bool { library.type == .music }
    /// Whether the server lists a music library's albums (and so has Albums and Genres): from 0.59, asked once per
    /// server. Until it answers, Browse and Artists.
    private var offersAlbums: Bool { model.offersMusicAlbums == true }
    /// A genre's own page is that genre's (its title, and a music genre's Play), so it offers no other.
    private var showsGenreFilter: Bool { initialGenre == nil }
    /// The genres the filter offers for what the grid lists.
    private var filterGenres: [GenreCount] { listsAlbums ? albumGenres : genres }
    /// What the grid lists: a music library's albums, or the library's titles (a music library's are its artists).
    private var listsAlbums: Bool { showing == .albums }

    var body: some View {
        ZStack {
            // A music library's artists and albums share the one grid, loaded afresh for each.
            if opened.contains(.all) || opened.contains(.albums) { kept(showing == .albums ? .albums : .all) { allTitles } }
            if opened.contains(.genres) { kept(.genres) { genreTiles } }
            if opened.contains(.browse) {
                kept(.browse) {
                    if music {
                        MusicLibraryView(library: library) { viewSwitch }
                    } else {
                        LibraryBrowseView(library: library, showAll: { showing = .all }) { viewSwitch }
                    }
                }
            }
        }
        .navigationTitle(initialGenre ?? library.name)
        .pageSwitch($showing, switchOptions)
        #if os(macOS)
        .pageSearch($search, prompt: t("Search {library}", ["library": library.name]))
        .task(id: search) {
            // Searching shows the grid; a pause in typing before asking.
            guard search != searchedFor else { return }
            searchedFor = search
            guard (try? await Task.sleep(for: .milliseconds(300))) != nil else { return }
            // Searching shows the grid. This load is the grid's first if it hasn't been opened yet (it waits for it).
            if searchWords != nil, showing != .all, showing != .albums { showing = .all }
            await reload()
        }
        #endif
        #if !os(macOS)
        .pageActions {
            if showing == .all || showing == .albums { sortAndFilter }
        }
        #endif
        .task {
            // For the toolbar's genre menu and the Genres view, once; a genre's own page needs neither.
            guard showsGenreFilter else { return }
            if music {
                await model.checkMusicAlbums(library: library)
                if genres.isEmpty { genres = await (try? model.client?.libraryGenres(library.id)) ?? [] }
                if offersAlbums, albumGenres.isEmpty { albumGenres = ((try? await model.client?.libraryAlbumGenres(library.id)) ?? nil) ?? [] }
            } else if hasGenres, genres.isEmpty {
                genres = await (try? model.client?.libraryGenres(library.id)) ?? []
            }
        }
        .onChange(of: showing) { _, new in
            opened.insert(new)
            // A music library's artists and albums share the grid: it starts over for the other, without the other's
            // genre (they're counted differently) or a sort it doesn't have.
            if music, new == .all || new == .albums, let holds = gridHoldsAlbums, holds != (new == .albums) {
                if initialGenre == nil { genre = nil }
                if !content.sorts.contains(sort) { sort = .title }
                Task { await reload() }
            }
        }
        // A server without albums: back to Browse from Albums or Genres.
        .onChange(of: model.offersMusicAlbums) { _, offered in
            if music, offered == false, showing == .albums || showing == .genres { showing = .browse }
        }
    }

    /// One of the views, shown when it's the one picked and otherwise kept out of sight, touch and VoiceOver.
    private func kept(_ view: Showing, @ViewBuilder _ content: () -> some View) -> some View {
        let current = showing == view
        return content()
            .opacity(current ? 1 : 0)
            .allowsHitTesting(current)
            .accessibilityHidden(!current)
            .zIndex(current ? 1 : 0)
    }

    /// Browse, All (movies, shows or folders) and Genres, as one segmented control at the top of each view. On the Mac
    /// it's in the window's toolbar instead, as `PageTabs` (`switchOptions`), as Mac apps put theirs.
    @ViewBuilder
    private var viewSwitch: some View {
        #if !os(macOS)
        switchControl
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 4)
        #endif
    }

    /// The views the switch offers, in order, with their names.
    private var switchOptions: [(Showing, String)] {
        guard (Self.hasBrowse(library, genre: initialGenre) || hasGenres) && !(music && initialGenre != nil) else { return [] }
        var options: [(Showing, String)] = []
        if Self.hasBrowse(library, genre: initialGenre) { options.append((.browse, t(Words.browse))) }
        options.append((.all, music ? t(Words.artists) : library.type == .tvShows ? t("All shows") : library.type == .homeVideos ? t("All folders") : t("All movies")))
        if music, offersAlbums { options.append((.albums, t(Words.albums))) }
        if hasGenres { options.append((.genres, t(Words.genres))) }
        return options
    }

    @ViewBuilder
    private var switchControl: some View {
        if (Self.hasBrowse(library, genre: initialGenre) || hasGenres) && !(music && initialGenre != nil) {
            Picker(t("Show"), selection: $showing) {
                if Self.hasBrowse(library, genre: initialGenre) { Text(t(Words.browse)).tag(Showing.browse) }
                Text(music ? t(Words.artists) : library.type == .tvShows ? t("All shows") : library.type == .homeVideos ? t("All folders") : t("All movies")).tag(Showing.all)
                if music, offersAlbums { Text(t(Words.albums)).tag(Showing.albums) }
                if hasGenres { Text(t(Words.genres)).tag(Showing.genres) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
        }
    }

    private var genreTiles: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 12) {
                viewSwitch
                LibraryGenreGrid(library: library, genres: music ? albumGenres : genres)
            }
            .padding(.bottom, 32)
        }
        .background(RoomBackground(ambient: items.first?.glowColor, intensity: 0.18))
        .refreshable {
            if music {
                albumGenres = ((try? await model.client?.libraryAlbumGenres(library.id)) ?? nil) ?? albumGenres
            } else {
                genres = await (try? model.client?.libraryGenres(library.id)) ?? genres
            }
        }
    }

    private var allTitles: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 12) {
                viewSwitch
                titles
            }
            .padding(.bottom, 32)
        }
        .background(RoomBackground(ambient: items.first?.glowColor, intensity: 0.18))
        .refreshable { await reload() }
        .task {
            // The sort and genre a link asked for, before the first page.
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
            // Already loading (a search switched to the grid and loads it): not a second request.
            guard !isLoading else { return }
            await reload()
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
        HStack(spacing: 10) {
            Text(content.count(total))
                .font(.subheadline.weight(.medium))
                .foregroundStyle(Color.usher)
                .monospacedDigit()
                .opacity(items.isEmpty ? 0 : 1)
            #if os(macOS)
            // On the Mac, sort and genre sit on the page above what they change; the toolbar keeps the page's
            // switch and search.
            Spacer(minLength: 0)
            sortMenu
            if showsGenreFilter, !filterGenres.isEmpty { genreMenu }
            #endif
        }
        .padding(.horizontal, Layout.edge)
        if listsAlbums, let genre = initialGenre {
            // A genre plays as a station: up to 200 of its tracks, in order or shuffled.
            HStack(spacing: 10) {
                Button { Task { await model.playGenre(genre, in: library, shuffled: false, router: router) } } label: {
                    Label(t(Words.play), systemImage: Symbol.play)
                }
                .buttonStyle(ActionButtonStyle(ambient: items.first?.ambientColor, prominent: true))
                Button { Task { await model.playGenre(genre, in: library, shuffled: true, router: router) } } label: {
                    Label(t(Words.shuffle), systemImage: Symbol.shuffle)
                }
                .buttonStyle(ActionButtonStyle(ambient: items.first?.glowColor))
            }
            .padding(.horizontal, Layout.edge)
            .disabled(items.isEmpty)
        }
        if let error, items.isEmpty {
            StatusView(state: .failed(error) { Task { await reload() } })
                .frame(height: 400)
        } else if items.isEmpty && !isLoading {
            StatusView(state: .empty(searchWords.map { t("Nothing found for “{query}”.", ["query": $0]) } ?? genre.map(noneInGenre) ?? t("Nothing in this library yet.")))
                .frame(height: 400)
        } else {
            LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                    Group {
                        if listsAlbums { AlbumCard(item: item, showsArtist: true) }
                        else if music { ArtistCard(item: item) }
                        else { PosterCard(item: item) }
                    }
                    .onAppear { loadMoreIfNeeded(at: index) }
                }
            }
            .padding(.horizontal, Layout.edge)
            if isLoading {
                ProgressView().frame(maxWidth: .infinity).padding(24)
            }
        }
    }

    #if os(macOS)
    /// Sort by, showing the order now.
    private var sortMenu: some View {
        PageChoice(icon: "arrow.up.arrow.down", title: t("Sort by"), selection: $sort,
                   options: content.sorts.map { ($0, $0.label) })
    }

    /// The genre shown, or all of them.
    private var genreMenu: some View {
        PageChoice(icon: genre == nil ? "line.3.horizontal.decrease" : "line.3.horizontal.decrease.circle.fill",
                   title: t("Genre"), selection: $genre,
                   options: [(String?.none, t("All genres"))] + filterGenres.map { (Optional($0.name), "\($0.name) (\($0.count))") },
                   shown: genre ?? t("All genres"), chosen: genre != nil)
    }
    #endif

    private var sortAndFilter: some View {
        Menu {
            Picker(t("Sort by"), selection: $sort) {
                ForEach(content.sorts) { option in
                    Text(option.label).tag(option)
                }
            }
            if showsGenreFilter, !filterGenres.isEmpty {
                Picker(t("Genre"), selection: $genre) {
                    Text(t("All genres")).tag(String?.none)
                    ForEach(filterGenres, id: \.name) { g in
                        Text("\(g.name) (\(g.count))").tag(Optional(g.name))
                    }
                }
                .pickerStyle(.menu)
            }
        } label: {
            Label(t("Sort and filter"), systemImage: genre == nil ? "line.3.horizontal.decrease.circle" : "line.3.horizontal.decrease.circle.fill")
        }
    }

    /// What the grid lists now, for its sorts and count.
    /// "No Rock albums here."
    private func noneInGenre(_ genre: String) -> String {
        switch content {
        case .titles: t("No {genre} titles here.", ["genre": genre])
        case .artists: t("No {genre} artists here.", ["genre": genre])
        case .albums: t("No {genre} albums here.", ["genre": genre])
        }
    }

    private var content: LibraryGridContent { listsAlbums ? .albums : music ? .artists : .titles }

    /// A page of what the grid lists, as it's sorted and filtered now.
    private func fetch(_ client: FlexarrClient, skip: Int, take: Int) async throws -> Paged<ItemSummary> {
        if listsAlbums {
            guard let page = try await client.libraryAlbums(library.id, sort: sort, genre: genre, search: searchWords, skip: skip, take: take) else {
                throw FlexarrClientError.http(status: 404, message: nil)
            }
            return page
        }
        return try await client.libraryItems(library.id, sort: sort, genre: genre, search: searchWords, skip: skip, take: take)
    }

    private var searchWords: String? {
        let words = search.trimmingCharacters(in: .whitespaces)
        return words.isEmpty ? nil : words
    }

    private func reload() async {
        guard let client = model.client else { return }
        generation += 1
        let current = generation
        isLoading = true
        // A newer reload owns the spinner by now.
        defer { if current == generation { isLoading = false } }
        let token = model.refreshToken
        // What the grid is loading now, so a switch before it arrives starts it over (and drops this answer).
        gridHoldsAlbums = listsAlbums
        do {
            let page = try await fetch(client, skip: 0, take: Self.pageSize)
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
            guard let page = try? await fetch(client, skip: offset, take: min(wanted - offset, Self.maxPageSize)),
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
            if let page = try? await fetch(client, skip: skip, take: Self.pageSize),
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
            StatusView(state: .empty(t("No genres here yet. They come with each title’s details once a scan has found them.")))
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
        if library.type == .music { return t("{count, plural, one {# album} other {# albums}}", ["count": genre.count]) }
        return library.type == .tvShows
            ? t("{count, plural, one {# show} other {# shows}}", ["count": genre.count])
            : t("{count, plural, one {# movie} other {# movies}}", ["count": genre.count])
    }
}

/// A genre's tile: a backdrop from one of its titles (a wash without one), the name and how many there are. An
/// audiobook genre has its books' covers fanned instead (`BookGenreArt`).
struct GenreTile: View {
    @Environment(AppModel.self) private var model
    let genre: GenreCount
    let caption: String

    var body: some View {
        Color.seat
            .aspectRatio(16 / 9, contentMode: .fit)
            .overlay {
                if let covers = genre.covers {
                    // Smaller than the web's half, so on a phone's tile the covers stay clear of the name beneath.
                    BookGenreArt(covers: covers, fan: 0.4)
                } else {
                    RemoteImage(url: model.client?.imageURL(genre.backdrop, width: 640)) {
                        LinearGradient(colors: [Color.silver.opacity(0.3), Color.seat], startPoint: .topLeading, endPoint: .bottomTrailing)
                    }
                }
            }
            .overlay {
                LinearGradient(colors: [Color.room.opacity(0.92), Color.room.opacity(0.1)], startPoint: .bottom, endPoint: .center)
            }
            .overlay(alignment: .bottomLeading) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(genre.name)
                        .font(.marquee(20, weight: .heavy, scales: false))
                        .foregroundStyle(Color.screen)
                    Text(caption)
                        .font(.caption.weight(.medium))
                        .foregroundStyle(Color.usher)
                }
                .lineLimit(1)
                .padding(10)
            }
            .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
    }
}
