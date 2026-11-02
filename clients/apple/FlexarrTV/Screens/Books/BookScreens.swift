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
/// books, each a row, in the profile's order (Settings › Home & browse). With no shelf left to show (all switched off,
/// or only empty ones on), every book in a grid instead. On the library's tab it has the Browse · All books · Genres
/// switch at the top; opened from elsewhere, the library's name and a row of its genres.
struct BooksLibraryView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.focusesContent) private var arriving
    let library: Library
    /// On the library's tab, the Browse, All books and Genres switch.
    var view: Binding<LibraryOpening>?
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
    @FocusState private var focusedBook: String?

    private static let pageSize = 60
    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 20) {
                if let view {
                    LibraryViewSwitch(library: library, view: view)
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
        .ignoresSafeArea(edges: view == nil ? .horizontal : [.top, .horizontal])
        .background(RoomBackground(ambient: shelves?.continueListening.first?.glowColor ?? shelves?.recentlyAdded.first?.glowColor ?? books.first?.glowColor, intensity: 0.18))
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
        // The top row takes focus from the switch as the tab opens, as a film library's first title does.
        let first = view != nil && shelf == shelves.shownShelves.first
        switch shelf {
        case .continueListening: bookRow("Continue listening", shelves.continueListening, first: first)
        case .nextInSeries: bookRow("Next in series", shelves.nextInSeries, first: first)
        case .recentlyAdded: bookRow("Recently added", shelves.recentlyAdded, first: first)
        case .series:
            if !shelves.series.isEmpty {
                ShelfRow(title: "Series", ids: shelves.series.map(\.id), column: $column, takesArrivingFocus: first) { focus in
                    ForEach(shelves.series) { series in
                        SeriesCard(series: series, focus: focus) { router.push(.bookSeries(id: series.id, name: series.name)) }
                    }
                }
            }
        case .authors: peopleRow("Authors", shelves.authors, first: first)
        case .finished: bookRow("Finished", shelves.finished, first: first)
        }
    }

    @ViewBuilder
    private func bookRow(_ title: String, _ items: [ItemSummary], first: Bool) -> some View {
        if !items.isEmpty {
            ShelfRow(title: title, ids: items.map(\.id), column: $column, takesArrivingFocus: first) { focus in
                ForEach(items) { book in BookCard(item: book, focus: focus) { router.push(.item(book)) } }
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
            }
        }
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
        guard view != nil, arriving.wrappedValue, let first = books.first else { return }
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
/// then its description, more in the series and by the author, and last its chapters (each plays from its start).
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
                        if let chapters = book?.chapters, chapters.count > 1 { chapterList(chapters, item: item) }
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
                        Label(playLabel(item), systemImage: model.music.current?.id == item.id && model.music.isPlaying ? "pause.fill" : "play.fill")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: item.ambientColor, prominent: true))
                    .focused($playFocused)
                    // Only with somewhere to start over from; part way through, it asks first.
                    if position > 0 || item.userData.watched {
                        Button {
                            if position > 0, !item.userData.watched { confirmingStartOver = true } else { Task { await model.listenAgain(item) } }
                        } label: { Label("Start over", systemImage: "arrow.counterclockwise") }
                            .buttonStyle(ActionButtonStyle(iconOnly: true))
                            .alert("Start over?", isPresented: $confirmingStartOver) {
                                Button("Start over", role: .destructive) { Task { await model.listenAgain(item) } }
                                Button("Cancel", role: .cancel) {}
                            } message: {
                                Text("You're at \(Format.timestamp(position)).")
                            }
                    }
                    if !model.isKids {
                        // Finished, or (when it isn't) unread: no place and not finished, as if never started. Not
                        // while it plays, which would set the place again.
                        let unread = item.userData.watched || (position > 0 && model.music.current?.id != item.id)
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
                        if unread, !item.userData.watched {
                            Button {
                                Task {
                                    await model.setBookFinished(item, false)
                                    await load()
                                }
                            } label: { Label("Mark as unread", systemImage: "book.closed") }
                                .buttonStyle(ActionButtonStyle(iconOnly: true))
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

/// A series in reading order, the next book to read first to focus.
struct BookSeriesView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let id: String
    let name: String
    @State private var series: BookSeries?
    @State private var error: String?
    @FocusState private var focused: String?

    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 40) {
                Text(series?.name ?? name)
                    .font(.marquee(76, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .padding(.top, 20)
                if let series {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                        ForEach(series.books) { entry in
                            BookCard(item: entry.book, focus: $focused, sequence: entry.sequence) { router.push(.item(entry.book)) }
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
        .background(RoomBackground(ambient: series?.books.first?.book.glowColor, intensity: 0.2))
        .task { await load() }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            series = try await client.bookSeries(id)
            focused = series?.nextId
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}

/// An audiobooks library's books in a grid, with the sorts as pills: All books on the library's tab, under the
/// Browse · All books · Genres switch, or one genre's books under its name, Back going to the genres.
struct BookGridView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.focusesContent) private var arriving
    let library: Library
    var genre: String?
    /// On the library's tab, the Browse, All books and Genres switch.
    var view: Binding<LibraryOpening>?
    @State private var sort: BookSort = .title
    @State private var books: [ItemSummary] = []
    @State private var total = 0
    @State private var loaded = false
    @State private var loadedToken: Int?
    @State private var loadingMore = false
    @State private var error: String?
    /// Bumped by each new sort, so an answer for the old one is dropped.
    @State private var generation = 0
    @FocusState private var focused: String?

    /// The sorts a remote can reach in a row; the phones' menus have the rest.
    private static let sorts: [BookSort] = [.title, .author, .added, .listened]
    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    private var filter: BookFilter { BookFilter(genre: genre, sort: sort, descending: sort.descendingByDefault) }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 40) {
                header
                if loaded, books.isEmpty {
                    StatusView(state: .empty("No books yet.")).frame(height: 500)
                } else if loaded {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 56) {
                        ForEach(Array(books.enumerated()), id: \.element.id) { index, book in
                            BookCard(item: book, focus: $focused) { router.push(.item(book)) }
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
            .padding(.horizontal, Layout.edge)
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
        .onChange(of: sort) {
            books = []
            loaded = false
            Task { await load() }
        }
        .onChange(of: arriving.wrappedValue, focusFirstBookOnArrival)
        .onChange(of: books.first?.id, focusFirstBookOnArrival)
    }

    private var header: some View {
        HStack(alignment: .lastTextBaseline, spacing: 30) {
            VStack(alignment: .leading, spacing: view == nil ? 4 : 16) {
                if let view {
                    LibraryViewSwitch(library: library, view: view).focusSection()
                } else {
                    Text(genre ?? library.name)
                        .font(.marquee(76, weight: .heavy))
                        .foregroundStyle(Color.screen)
                }
                if loaded {
                    Text(total == 1 ? "1 book" : "\(total.formatted()) books")
                        .font(.system(size: 24, weight: .medium))
                        .foregroundStyle(Color.usher)
                        .monospacedDigit()
                }
            }
            Spacer()
            HStack(spacing: 18) {
                ForEach(Self.sorts) { option in
                    Button(option.label) { sort = option }
                        .buttonStyle(PillButtonStyle(selected: sort == option))
                }
            }
            .focusSection()
        }
        .padding(.top, view == nil ? 20 : LibraryViewSwitch.topInset)
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
            let page = try await client.books(library.id, filter: filter, take: min(200, max(60, books.count)))
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
        guard let client = model.client, !loadingMore, loaded, books.count < total else { return }
        let current = generation
        let skip = books.count
        loadingMore = true
        defer { loadingMore = false }
        if let page = try? await client.books(library.id, filter: filter, skip: skip, take: 60), current == generation, skip == books.count {
            let known = Set(books.map(\.id))
            books += page.items.filter { !known.contains($0.id) }
            total = page.total
        }
    }
}
