import FlexarrKit
import SwiftUI

// Audiobooks on the Apple TV: a library's shelves, a book's page and a series in reading order. Books play in the
// shared audio player's book mode, with a big-screen Now Playing of their own (see NowPlayingView).

/// A book: its square cover with how far in it is, the title and author beneath.
struct BookCard: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    var width: CGFloat = Layout.posterWidth
    var focus: FocusState<String?>.Binding?
    /// The series number instead of the author, in a series.
    var sequence: String?
    let action: () -> Void

    var body: some View {
        ArtworkButton(ambient: item.glowColor, focus: focus, focusID: item.id, action: action) {
            SquareArtwork(item: item, size: width)
                .overlay(alignment: .bottom) {
                    if let progress = item.progress, !item.userData.watched {
                        ProgressBar(value: progress).padding(14)
                    }
                }
                .overlay(alignment: .topTrailing) {
                    if item.userData.watched { WatchedBadge().padding(12) }
                }
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(item.title)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.8))
                    .lineLimit(1)
                if let subtitle = sequence.map({ "Book \($0)" }) ?? item.bookAuthors {
                    Text(subtitle)
                        .font(.system(size: 21))
                        .foregroundStyle(Color.usher)
                        .lineLimit(1)
                }
            }
            .frame(width: width, alignment: .leading)
        }
        .contextMenu {
            Button { model.playBook(item) } label: { Label(item.isResumable ? "Resume" : "Play", systemImage: "play.fill") }
            if !model.isKids {
                if !item.userData.watched {
                    Button { Task { await model.setBookFinished(item, true) } } label: { Label("Mark as finished", systemImage: "checkmark.circle") }
                }
                // Unread: no place and not finished, as if never started. Not while it plays, which would set the place again.
                if item.userData.watched || item.isResumable, model.music.current?.id != item.id {
                    Button { Task { await model.setBookFinished(item, false) } } label: { Label("Mark as unread", systemImage: "book.closed") }
                }
            }
        }
    }
}

/// An audiobooks library's Browse: Continue listening, Next in series, Recently added, series, authors and finished
/// books, each a row, in the profile's order (Settings › Home & browse), under a hero of the books part way through
/// and the newest (servers from 0.59). With no shelf left to show (all switched off, or only empty ones on), every
/// book in a grid instead. On the library's tab it has the Browse · All books · Genres · Series · Authors switch at
/// the top; opened from elsewhere, the library's name and a row of its genres. Recently added, Series, Authors and
/// Finished end with See all, which opens their books, series or authors on top.
struct BooksLibraryView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.focusesContent) private var arriving
    let library: Library
    /// On the library's tab, the Browse, All books, Genres, Series and Authors switch.
    var view: Binding<BookOpening>?
    @State private var shelves: BookShelves?
    @State private var error: String?
    @State private var loadedAt: Date?
    @State private var loadedToken: Int?
    @State private var isVisible = false
    /// Every book (paged, by title), loaded only when no shelf has anything to show.
    @State private var books: [ItemSummary] = []
    @State private var booksTotal = 0
    @State private var isLoadingMore = false
    /// Where across a shelf focus last was, so up and down keep to it (see `RowFocus`).
    @State private var column = 0
    @State private var featuredIndex = 0
    @FocusState private var focusedBook: String?

    private static let pageSize = 60

    /// The hero's books (servers from 0.59): part way through, then the newest. None: no hero, the page as before.
    private var featuredBooks: [ItemSummary] { shelves?.featured ?? [] }

    private var featured: ItemSummary? {
        featuredBooks.isEmpty ? nil : featuredBooks[featuredIndex % featuredBooks.count]
    }

    /// Above the hero, under the sidebar's label: the switch on the library's tab, else the library's name.
    private var heroTop: AnyView {
        if let view {
            AnyView(BookViewSwitch(view: view))
        } else {
            AnyView(Text(library.name)
                .font(.marquee(48, weight: .heavy))
                .foregroundStyle(Color.screen))
        }
    }
    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 20) {
                if !featuredBooks.isEmpty {
                    HomeHero(items: featuredBooks, overviews: shelves?.featuredOverviews ?? [:], index: $featuredIndex, top: heroTop)
                        .padding(.horizontal, -Layout.edge)
                } else if let view {
                    BookViewSwitch(view: view)
                        .focusSection()
                        .padding(.top, LibraryViewSwitch.topInset)
                        .padding(.bottom, 20)
                } else {
                    Text(library.name)
                        .font(.marquee(76, weight: .heavy))
                        .foregroundStyle(Color.screen)
                        .padding(.top, 20)
                }
                if let shelves {
                    if !shelves.shownShelves.isEmpty {
                        // In the profile's order, without the shelves it switched off.
                        ForEach(shelves.shownShelves, id: \.self) { shelf(shelf: $0, shelves) }
                    } else if !books.isEmpty {
                        allBooks
                    } else {
                        StatusView(state: .empty("No books yet.")).frame(height: 500)
                    }
                    // The shelves carry the genres (the most-used 200), so the row costs nothing more to show. On the
                    // tab, the switch's Genres has them instead, as for films and shows.
                    let genres = shelves.genres.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
                    if view == nil, !shelves.shownShelves.isEmpty, !genres.isEmpty {
                        ShelfRow(title: "Genres", ids: genres.map(\.name), column: $column) { focus in
                            ForEach(genres, id: \.name) { genre in
                                GenreTile(name: genre.name, caption: genre.count == 1 ? "1 book" : "\(genre.count) books", backdrop: nil, focus: focus) {
                                    router.push(.library(library, .title, genre: genre.name))
                                }
                            }
                        }
                    }
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
        // Layout.edge from the screen's edge, as on the music and film pages (whose full-width grids reach past the
        // safe area): without this the screen's safe area comes on top of it. On the tab, the switch sits where it does
        // on the film pages.
        .ignoresSafeArea(edges: view == nil && featuredBooks.isEmpty ? .horizontal : [.top, .horizontal])
        .background(RoomBackground(ambient: featured?.glowColor ?? shelves?.continueListening.first?.glowColor ?? shelves?.recentlyAdded.first?.glowColor ?? books.first?.glowColor,
                                   intensity: featured == nil ? 0.18 : 0.28)
            .animation(.easeInOut(duration: 0.8), value: featured?.id))
        .task(id: model.refreshToken) {
            guard loadedToken != model.refreshToken || Freshness.isStale(loadedAt) else { return }
            await load()
        }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible, Freshness.isStale(loadedAt) { Task { await load() } }
        }
    }

    @ViewBuilder
    private func shelf(shelf: BookShelf, _ shelves: BookShelves) -> some View {
        // The top row takes focus from the switch as the tab opens, as a film library's first title does; with a hero,
        // its Play does instead.
        let first = view != nil && featuredBooks.isEmpty && shelf == shelves.shownShelves.first
        switch shelf {
        // See all where the iPhone has it: the newest and finished books, every series and every author.
        case .continueListening: bookRow("Continue listening", shelves.continueListening, first: first)
        case .nextInSeries: bookRow("Next in series", shelves.nextInSeries, first: first)
        case .recentlyAdded: bookRow("Recently added", shelves.recentlyAdded, first: first, seeAll: .books(.added, .any))
        case .series:
            if !shelves.series.isEmpty {
                ShelfRow(title: "Series", ids: shelves.series.map(\.id), column: $column, takesArrivingFocus: first) { focus in
                    ForEach(shelves.series) { series in
                        SeriesCard(series: series, focus: focus) { router.push(.bookSeries(id: series.id, name: series.name)) }
                    }
                    seeAll(.series)
                }
            }
        case .authors: peopleRow("Authors", shelves.authors, first: first)
        case .finished: bookRow("Finished", shelves.finished, first: first, seeAll: .books(.title, .finished))
        }
    }

    @ViewBuilder
    private func bookRow(_ title: String, _ items: [ItemSummary], first: Bool, seeAll list: BookList? = nil) -> some View {
        if !items.isEmpty {
            ShelfRow(title: title, ids: items.map(\.id), column: $column, takesArrivingFocus: first) { focus in
                ForEach(items) { book in BookCard(item: book, focus: focus) { router.push(.item(book)) } }
                if let list { seeAll(list) }
            }
        }
    }

    @ViewBuilder
    private func peopleRow(_ title: String, _ people: [BookPerson], first: Bool) -> some View {
        if !people.isEmpty {
            ShelfRow(title: title, ids: people.map(\.id), column: $column, takesArrivingFocus: first) { focus in
                ForEach(people) { person in
                    PersonCard(name: person.name, detail: person.bookCount == 1 ? "1 book" : "\(person.bookCount) books", image: person.image,
                               focus: focus, focusID: person.id) {
                        router.push(.person(id: person.id, name: person.name))
                    }
                }
                seeAll(.authors)
            }
        }
    }

    /// The end of a shelf: its books, series or authors on top, so Back comes back here.
    private func seeAll(_ list: BookList) -> some View {
        SeeAllCard(style: .poster, square: true) { router.push(.bookList(library, list)) }
    }

    private var allBooks: some View {
        LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
            ForEach(Array(books.enumerated()), id: \.element.id) { index, book in
                BookCard(item: book, focus: $focusedBook) { router.push(.item(book)) }
                    .onAppear { loadMoreIfNeeded(at: index) }
            }
        }
        .padding(.top, 20)
        .focusSection()
        .onAppear(perform: focusFirstBookOnArrival)
        .onChange(of: arriving.wrappedValue, focusFirstBookOnArrival)
    }

    /// From the switch, as the tab opens (see `focusesContent`): to the first book.
    private func focusFirstBookOnArrival() {
        guard view != nil, featuredBooks.isEmpty, arriving.wrappedValue, let first = books.first else { return }
        arriving.wrappedValue = false
        focusedBook = first.id
    }

    private func loadMoreIfNeeded(at index: Int) {
        guard let client = model.client, !isLoadingMore, books.count < booksTotal, index >= books.count - 18 else { return }
        isLoadingMore = true
        let skip = books.count
        Task {
            defer { isLoadingMore = false }
            guard let page = try? await client.books(library.id, skip: skip, take: Self.pageSize), skip == books.count else { return }
            let known = Set(books.map(\.id))
            books += page.items.filter { !known.contains($0.id) }
            booksTotal = page.total
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        let token = model.refreshToken
        do {
            let loaded = try await client.bookShelves(library.id)
            if loaded.shownShelves.isEmpty {
                let page = try await client.books(library.id, take: Self.pageSize)
                books = page.items
                booksTotal = page.total
            } else {
                books = []
                booksTotal = 0
            }
            shelves = loaded
            error = nil
            loadedAt = Date()
            loadedToken = token
        } catch {
            if shelves == nil { self.error = ConnectionError(error).message }
        }
    }
}

/// One of the library page's shelves: its heading and cards in a row, the cards taking the row's own focus state
/// (a book can be on more than one shelf) so up and down land in the column focus left (see `RowFocus`).
private struct ShelfRow<Content: View>: View {
    let title: String
    let ids: [String]
    @Binding var column: Int
    /// The top row on the library's tab: takes focus from the switch as the tab opens (see `focusesContent`).
    var takesArrivingFocus = false
    @ViewBuilder let content: (FocusState<String?>.Binding) -> Content
    @FocusState private var focusedCard: String?
    @Environment(\.focusesContent) private var arriving

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(title)
                .font(.marquee(40, weight: .bold))
                .foregroundStyle(Color.screen)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) { content($focusedCard) }
                    .padding(.top, 28)
                    .padding(.bottom, 40)
            }
            .scrollClipDisabled()
            .focusSection()
            .modifier(RowFocus(ids: ids, column: $column, focus: $focusedCard))
        }
        .onAppear(perform: takeFocusOnArrival)
        .onChange(of: arriving.wrappedValue, takeFocusOnArrival)
    }

    private func takeFocusOnArrival() {
        guard takesArrivingFocus, arriving.wrappedValue, let first = ids.first else { return }
        arriving.wrappedValue = false
        focusedCard = first
    }
}

/// A series: its first book's cover with the name and how many books beneath.
struct SeriesCard: View {
    @Environment(AppModel.self) private var model
    let series: BookSeriesSummary
    /// Shared focus state, so the row can steer focus to this card (see `RowFocus`).
    var focus: FocusState<String?>.Binding? = nil
    let action: () -> Void

    var body: some View {
        ArtworkButton(ambient: .silver, focus: focus, focusID: series.id, action: action) {
            RemoteImage(url: model.client?.imageURL(series.covers.first?.poster, width: 500)) {
                ZStack {
                    Color.seat
                    Text(series.name)
                        .font(.marquee(30, weight: .bold))
                        .foregroundStyle(Color.screen.opacity(0.8))
                        .multilineTextAlignment(.center)
                        .padding(20)
                }
            }
            .frame(width: Layout.posterWidth, height: Layout.posterWidth)
            .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(series.name)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.8))
                Text(series.bookCount == 1 ? "1 book" : "\(series.bookCount) books")
                    .font(.system(size: 21))
                    .foregroundStyle(Color.usher)
            }
            .lineLimit(1)
            .frame(width: Layout.posterWidth, alignment: .leading)
        }
    }
}

/// A book's page: the cover beside its title, its series and people (each opens their page), length, Play or Resume;
/// then its description, more in the series, by the author and read by the narrator, its chapters (each plays from its
/// start) and its bookmarks (each plays from its place; they're added and changed on the phones).
struct BookView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let id: String
    let preview: ItemSummary?
    @State private var detail: ItemDetail?
    @State private var error: String?
    @State private var loadedToken: Int?
    @FocusState private var playFocused: Bool
    /// Start over was pressed part way through: it asks first.
    @State private var confirmingStartOver = false

    private var item: ItemSummary? { detail?.item ?? preview }
    private var book: BookDetail? { detail?.book }
    private var position: Double { model.music.current?.id == id ? model.music.position : item?.userData.positionSeconds ?? 0 }
    private var duration: Double { book?.durationSeconds ?? item?.book?.durationSeconds ?? item?.effectiveDuration ?? 0 }

    var body: some View {
        ZStack {
            RoomBackground(ambient: item?.glowColor, intensity: 0.3)
            if let item {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 60) {
                        header(item)
                        if let overview = detail?.overview, !overview.isEmpty {
                            Text(overview)
                                .font(.system(size: 28))
                                .foregroundStyle(Color.screen.opacity(0.85))
                                .frame(maxWidth: 1200, alignment: .leading)
                                .padding(.horizontal, Layout.edge)
                        }
                        if let authors = book?.authorCards, !authors.isEmpty { authorCards(authors, ambient: item.glowColor) }
                        if let more = book?.moreInSeries, !more.isEmpty { row("More in the series", more) }
                        if let more = book?.byAuthor, !more.isEmpty { row(credits(.author).first.map { "More by \($0.name)" } ?? "More by the author", more) }
                        if let more = book?.readByNarrator, !more.isEmpty {
                            row(credits(.narrator).first.map { "More read by \($0.name)" } ?? "More from the narrator", more)
                        }
                        if let chapters = book?.chapters, chapters.count > 1 { chapterList(chapters, item: item) }
                        if let book, !book.bookmarks.isEmpty { bookmarkList(book.bookmarks, chapters: book.chapters, item: item) }
                    }
                    .padding(.vertical, 60)
                }
                .scrollClipDisabled()
            } else if let error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .defaultFocus($playFocused, true)
        .task(id: model.refreshToken) {
            guard loadedToken != model.refreshToken else { return }
            await load()
        }
    }

    private func header(_ item: ItemSummary) -> some View {
        HStack(alignment: .top, spacing: 80) {
            SquareArtwork(item: item, size: 520)
            VStack(alignment: .leading, spacing: 16) {
                Text(item.title)
                    .font(.marquee(64, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .lineLimit(2)
                    .minimumScaleFactor(0.6)
                if let subtitle = book?.subtitle {
                    Text(subtitle).font(.system(size: 30, weight: .medium)).foregroundStyle(Color.screen.opacity(0.8))
                }
                links(item)
                FactsLine(facts: facts(item))
                HStack(spacing: 30) {
                    Button {
                        if model.music.current?.id == item.id { model.music.togglePlayPause() } else { model.playBook(item) }
                    } label: {
                        Label(playLabel(item), systemImage: model.music.current?.id == item.id && model.music.isPlaying ? Symbol.pause : Symbol.play)
                    }
                    .buttonStyle(ActionButtonStyle(ambient: item.ambientColor, prominent: true))
                    .focused($playFocused)
                    // After Play, in `BookActions` order: Start over, then Finished (or Unread once it's finished), then ⋯.
                    ForEach(BookActions(hasProgress: position > 0 && !item.userData.watched, finished: item.userData.watched,
                                        isKids: model.isKids, platform: .tv, hasMenu: model.canDeleteBooks).buttons.dropFirst(),
                            id: \.self) { button in
                        switch button {
                        case .startOver:
                            // Part way through, it asks first, and offers Mark as unread there (as the phones do).
                            Button {
                                if position > 0, !item.userData.watched { confirmingStartOver = true } else { Task { await model.listenAgain(item) } }
                            } label: { Label("Start over", systemImage: Symbol.startOver) }
                                .buttonStyle(ActionButtonStyle(iconOnly: true))
                                .alert("Start over?", isPresented: $confirmingStartOver) {
                                    Button("Start over", role: .destructive) { Task { await model.listenAgain(item) } }
                                    // Not while it plays, which would set the place again.
                                    if !model.isKids, model.music.current?.id != item.id {
                                        Button("Mark as unread") {
                                            Task {
                                                await model.setBookFinished(item, false)
                                                await load()
                                            }
                                        }
                                    }
                                    Button("Cancel", role: .cancel) {}
                                } message: {
                                    Text("You're at \(Format.timestamp(position)).")
                                }
                        case .finished:
                            Button {
                                Task {
                                    await model.setBookFinished(item, !item.userData.watched)
                                    await load()
                                }
                            } label: {
                                // A closed book for unread: an arrow would look like Start over's beside it.
                                Label(item.userData.watched ? "Mark as unread" : "Mark as finished",
                                      systemImage: item.userData.watched ? "book.closed" : "checkmark")
                            }
                            .buttonStyle(ActionButtonStyle(iconOnly: true))
                        case .more:
                            BookMoreMenu(item: item)
                                .buttonStyle(ActionButtonStyle(iconOnly: true))
                        case .play, .download:
                            EmptyView()
                        }
                    }
                }
                .padding(.top, 20)
                .focusSection()
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, Layout.edge)
    }

    private func credits(_ role: CreditRole) -> [Credit] {
        Array((detail?.credits ?? []).filter { $0.role == role }.prefix(3))
    }

    /// The series, authors and narrators, each a button to their page; names the server hasn't linked yet are text.
    @ViewBuilder
    private func links(_ item: ItemSummary) -> some View {
        let authors = credits(.author), narrators = credits(.narrator)
        let series = book?.series.first
        if series != nil || !authors.isEmpty || !narrators.isEmpty {
            // Scrolls sideways when the pills don't fit (a focused one comes into view).
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 16) {
                    if let series {
                        Button { router.push(.bookSeries(id: series.id, name: series.name)) } label: {
                            Label([series.name, series.placeLabel].compactMap { $0 }.joined(separator: ", "), systemImage: "books.vertical")
                        }
                        .accessibilityHint("Opens the series")
                    }
                    ForEach(authors, id: \.personId) { credit in
                        Button(credit.name) { router.push(.person(id: credit.personId, name: credit.name)) }
                    }
                    // Names the server hasn't linked to a page yet are still shown.
                    if authors.isEmpty, let names = item.bookAuthors { plainLink(names) }
                    ForEach(narrators, id: \.personId) { credit in
                        Button("Read by \(credit.name)") { router.push(.person(id: credit.personId, name: credit.name)) }
                    }
                    if narrators.isEmpty, let narrator = item.book?.narrator { plainLink("Read by \(narrator)") }
                }
                .buttonStyle(PillButtonStyle(selected: false, ambient: item.glowColor))
                .lineLimit(1)
                // Room inside the clip for a focused pill's lift, so pills scrolled away don't draw over the cover.
                .padding(.vertical, 20)
                .padding(.horizontal, 12)
            }
            .padding(.vertical, -20)
            .padding(.horizontal, -12)
            .focusSection()
        } else {
            let read = item.book?.narrator.map { "Read by \($0)" }
            Text([item.bookAuthors, read].compactMap { $0 }.joined(separator: "   "))
                .font(.system(size: 28, weight: .medium))
                .foregroundStyle(Color.usher)
                .lineLimit(2)
        }
    }

    /// A card for each author with a photo or biography: the photo, name and start of the biography; it opens their page.
    private func authorCards(_ authors: [BookAuthor], ambient: Color) -> some View {
        VStack(alignment: .leading, spacing: 24) {
            ForEach(authors) { author in
                Button { router.push(.person(id: author.id, name: author.name)) } label: {
                    HStack(alignment: .top, spacing: 30) {
                        RemoteImage(url: model.client?.imageURL(author.image, width: 240)) {
                            Color.seat.overlay {
                                Image(systemName: "person.fill").font(.system(size: 44)).foregroundStyle(Color.usher)
                            }
                        }
                        .frame(width: 120, height: 120)
                        .clipShape(Circle())
                        VStack(alignment: .leading, spacing: 8) {
                            Text(author.name)
                                .font(.system(size: 30, weight: .semibold))
                                .lineLimit(1)
                            if let biography = author.biography?.trimmingCharacters(in: .whitespacesAndNewlines), !biography.isEmpty {
                                Text(biography)
                                    .font(.system(size: 25))
                                    .lineSpacing(4)
                                    .lineLimit(3)
                                    .multilineTextAlignment(.leading)
                                    .opacity(0.8)
                            }
                        }
                        Spacer(minLength: 0)
                    }
                    .frame(maxWidth: 1140, alignment: .leading)
                }
                .buttonStyle(TextCardStyle(ambient: ambient, filled: true))
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(author.name)
                .accessibilityAddTraits(.isButton)
            }
        }
        .padding(.horizontal, Layout.edge - 30)
        .focusSection()
    }

    /// A name beside the pills that has no page to open.
    private func plainLink(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 28, weight: .medium))
            .foregroundStyle(Color.usher)
    }

    private func facts(_ item: ItemSummary) -> [String] {
        var facts = [Format.runtime(seconds: duration)].compactMap { $0 }
        let speed = book?.speed ?? model.bookListening?.speed ?? 1
        if position > 0, !item.userData.watched, let left = Format.remaining((duration - position) / max(0.5, speed)) { facts.append(left) }
        if item.userData.watched { facts.append("Finished") }
        return facts
    }

    private func playLabel(_ item: ItemSummary) -> String {
        if model.music.current?.id == item.id, model.music.isPlaying { return "Pause" }
        return position > 0 && !item.userData.watched ? "Resume from \(Format.timestamp(position))" : "Play"
    }

    private func chapterList(_ chapters: [BookChapter], item: ItemSummary) -> some View {
        let timeline = BookTimeline(files: [], chapters: chapters, duration: duration)
        let current = position > 0 ? timeline.chapterIndex(at: position) : nil
        return VStack(alignment: .leading, spacing: 8) {
            RowHeading(title: "Chapters").padding(.leading, -Layout.edge + 30)
            LazyVStack(alignment: .leading, spacing: 8) {
                ForEach(chapters) { chapter in
                    ChapterRow(chapter: chapter, isCurrent: chapter.index == current,
                               heard: (current ?? -1) > (chapters.firstIndex(of: chapter) ?? 0) || item.userData.watched) {
                        model.playBook(item, at: chapter.startSeconds)
                    }
                }
            }
            .frame(maxWidth: 1200)
            .focusSection()
        }
        .padding(.horizontal, Layout.edge - 30)
    }

    /// The bookmarks, in the book's order: the time, the note (or the chapter it's in), and the chapter beneath a note.
    private func bookmarkList(_ marks: [Bookmark], chapters: [BookChapter], item: ItemSummary) -> some View {
        let timeline = BookTimeline(files: [], chapters: chapters, duration: duration)
        return VStack(alignment: .leading, spacing: 8) {
            RowHeading(title: "Bookmarks").padding(.leading, -Layout.edge + 30)
            LazyVStack(alignment: .leading, spacing: 8) {
                ForEach(marks.sorted { $0.positionSeconds < $1.positionSeconds }) { mark in
                    let chapter = timeline.chapter(at: mark.positionSeconds)
                    Button { model.playBook(item, at: mark.positionSeconds) } label: {
                        HStack(alignment: .firstTextBaseline, spacing: 28) {
                            Text(Format.timestamp(mark.positionSeconds))
                                .font(.system(size: 24, weight: .semibold))
                                .monospacedDigit()
                                .frame(minWidth: 120, alignment: .leading)
                            VStack(alignment: .leading, spacing: 4) {
                                Text(mark.note ?? chapter?.title ?? "Bookmark")
                                    .font(.system(size: 28, weight: .medium))
                                    .lineLimit(2)
                                if mark.note != nil, let chapter {
                                    Text(chapter.title)
                                        .font(.system(size: 22))
                                        .lineLimit(1)
                                        .opacity(0.7)
                                }
                            }
                            Spacer(minLength: 0)
                        }
                    }
                    .buttonStyle(TrackRowStyle(isCurrent: false))
                    .accessibilityLabel("\(mark.note ?? chapter?.title ?? "Bookmark"), \(Format.timestamp(mark.positionSeconds))")
                }
            }
            .frame(maxWidth: 1200)
            .focusSection()
        }
        .padding(.horizontal, Layout.edge - 30)
    }

    private func row(_ title: String, _ items: [ItemSummary]) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            RowHeading(title: title)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                    ForEach(items) { book in BookCard(item: book) { router.push(.item(book)) } }
                }
                .padding(.horizontal, Layout.edge)
                .padding(.top, 28)
                .padding(.bottom, 40)
            }
            .scrollClipDisabled()
            .focusSection()
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        let token = model.refreshToken
        do {
            detail = try await client.item(id)
            loadedToken = token
            error = nil
        } catch {
            if detail == nil { self.error = ConnectionError(error).message }
        }
    }
}

/// A chapter in a list: its title, and its length; the one playing is marked, those heard are dimmed.
struct ChapterRow: View {
    let chapter: BookChapter
    let isCurrent: Bool
    var heard = false
    var isPlaying = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 28) {
                Group {
                    if isCurrent {
                        Image(systemName: isPlaying ? "speaker.wave.2.fill" : "speaker.fill").font(.system(size: 24, weight: .semibold))
                    } else {
                        Text(String(chapter.index + 1)).font(.system(size: 26, weight: .medium)).monospacedDigit()
                    }
                }
                .frame(width: 56, alignment: .trailing)
                Text(chapter.title)
                    .font(.system(size: 28, weight: isCurrent ? .bold : .medium))
                    .lineLimit(1)
                    .opacity(heard && !isCurrent ? 0.6 : 1)
                Spacer(minLength: 20)
                Text(Format.timestamp(chapter.durationSeconds))
                    .font(.system(size: 24, weight: .medium))
                    .monospacedDigit()
            }
        }
        .buttonStyle(TrackRowStyle(isCurrent: isCurrent))
    }
}

/// A series in reading order: how far through it this profile is, Play (or Resume) for the next book to read, the
/// series' description, then its books, the next one picked out.
struct BookSeriesView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let id: String
    let name: String
    @State private var series: BookSeries?
    @State private var error: String?
    @State private var loadedToken: Int?
    /// Books Audible lists in the series that the library doesn't have.
    @State private var gaps: BookSeriesGaps = .none
    @FocusState private var focused: String?
    @FocusState private var playFocused: Bool

    private var next: ItemSummary? {
        guard let series else { return nil }
        return series.books.first { $0.book.id == series.nextId }?.book
    }

    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 40) {
                VStack(alignment: .leading, spacing: 8) {
                    Text(series?.name ?? name)
                        .font(.marquee(76, weight: .heavy))
                        .foregroundStyle(Color.screen)
                    if let series {
                        let finished = series.books.filter { $0.book.userData.watched }.count
                        Text(finished > 0 ? "\(finished) of \(series.books.count) finished" : series.books.count == 1 ? "1 book" : "\(series.books.count) books")
                            .font(.system(size: 26, weight: .medium))
                            .foregroundStyle(Color.usher)
                            .monospacedDigit()
                    }
                }
                .padding(.top, 20)
                if let next {
                    // The next book to read, from its place if it's started (as the iPhone's series page has it).
                    Button { model.playBook(next) } label: {
                        Label(next.isResumable ? "Resume \(next.title)" : "Play \(next.title)", systemImage: Symbol.play)
                            .lineLimit(1)
                    }
                    .buttonStyle(ActionButtonStyle(ambient: next.ambientColor, prominent: true))
                    .focused($playFocused)
                    .frame(maxWidth: 1100, alignment: .leading)
                    .focusSection()
                }
                if let description = series?.description, !description.isEmpty {
                    Text(description)
                        .font(.system(size: 28))
                        .foregroundStyle(Color.screen.opacity(0.85))
                        .lineLimit(4)
                        .frame(maxWidth: 1200, alignment: .leading)
                }
                if let series {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                        ForEach(series.books) { entry in
                            BookCard(item: entry.book, focus: $focused, sequence: entry.sequence) { router.push(.item(entry.book)) }
                        }
                    }
                    .focusSection()
                    if !gaps.books.isEmpty {
                        VStack(alignment: .leading, spacing: 28) {
                            Text("Not in your library")
                                .font(.marquee(40, weight: .bold))
                                .foregroundStyle(Color.screen)
                            LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                                ForEach(gaps.books) { book in
                                    MissingBookCard(book: book)
                                }
                            }
                            .focusSection()
                        }
                    }
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
        .background(RoomBackground(ambient: next?.glowColor ?? series?.books.first?.book.glowColor, intensity: 0.2))
        // Again after playing or marking a book, which may move the next one on.
        .task(id: model.refreshToken) {
            guard loadedToken != model.refreshToken else { return }
            await load()
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        let token = model.refreshToken
        let first = series == nil
        do {
            series = try await client.bookSeries(id)
            loadedToken = token
            error = nil
            // Opening the series: Play for the next book takes focus, or with none (every book finished), the first book.
            if first {
                if next != nil { playFocused = true } else { focused = series?.books.first?.book.id }
            }
        } catch {
            if series == nil { self.error = ConnectionError(error).message }
        }
        // After the page: Audible being slow never holds it up.
        gaps = (try? await client.bookSeriesGaps(id)) ?? gaps
    }
}

/// A book of the series the library doesn't have: its cover, title, number and year. Focusable so the row can be
/// reached and read, but there's nothing to open.
struct MissingBookCard: View {
    @Environment(AppModel.self) private var model
    let book: MissingBook

    var body: some View {
        ArtworkButton(ambient: .silver, accessibilityText: [book.title, book.caption].compactMap(\.self).joined(separator: ", ")) {
        } artwork: {
            RemoteImage(url: model.client?.imageURL(book.cover, width: 400)) {
                ZStack {
                    LinearGradient(colors: [Color.silver.opacity(0.3), Color.seat], startPoint: .top, endPoint: .bottom)
                    Text(book.title)
                        .font(.marquee(26, weight: .bold))
                        .foregroundStyle(Color.screen.opacity(0.8))
                        .multilineTextAlignment(.center)
                        .padding(14)
                }
            }
            .frame(width: Layout.posterWidth, height: Layout.posterWidth)
            .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(book.title)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.8))
                if let caption = book.caption {
                    Text(caption).font(.system(size: 21)).foregroundStyle(Color.usher)
                }
            }
            .lineLimit(1)
            .frame(width: Layout.posterWidth, alignment: .leading)
        }
    }
}
