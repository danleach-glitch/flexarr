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
                Button { Task { await model.setBookFinished(item, !item.userData.watched) } } label: {
                    Label(item.userData.watched ? "Mark as not finished" : "Mark as finished", systemImage: "checkmark.circle")
                }
            }
        }
    }
}

/// An audiobooks library's tab: Continue listening first, then Next in series, Recently added, series, authors,
/// narrators and finished books, each a row.
struct BooksLibraryView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.scenePhase) private var scenePhase
    let library: Library
    var showsTitle = true
    @State private var shelves: BookShelves?
    @State private var error: String?
    @State private var loadedAt: Date?
    @State private var loadedToken: Int?
    @State private var isVisible = false

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 20) {
                if showsTitle {
                    Text(library.name)
                        .font(.marquee(76, weight: .heavy))
                        .foregroundStyle(Color.screen)
                        .padding(.horizontal, Layout.edge)
                        .padding(.top, 20)
                }
                if let shelves {
                    if shelves.isEmpty {
                        StatusView(state: .empty("No books yet.")).frame(height: 500)
                    }
                    bookRow("Continue listening", shelves.continueListening)
                    bookRow("Next in series", shelves.nextInSeries)
                    bookRow("Recently added", shelves.recentlyAdded)
                    if !shelves.series.isEmpty {
                        row("Series") {
                            ForEach(shelves.series) { series in
                                SeriesCard(series: series) { router.push(.bookSeries(id: series.id, name: series.name)) }
                            }
                        }
                    }
                    peopleRow("Authors", shelves.authors)
                    peopleRow("Narrators", shelves.narrators)
                    bookRow("Finished", shelves.finished)
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 500)
                } else {
                    StatusView(state: .loading).frame(height: 500)
                }
            }
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .background(RoomBackground(ambient: shelves?.continueListening.first?.glowColor ?? shelves?.recentlyAdded.first?.glowColor, intensity: 0.18))
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
    private func bookRow(_ title: String, _ items: [ItemSummary]) -> some View {
        if !items.isEmpty {
            row(title) {
                ForEach(items) { book in BookCard(item: book) { router.push(.item(book)) } }
            }
        }
    }

    @ViewBuilder
    private func peopleRow(_ title: String, _ people: [BookPerson]) -> some View {
        if !people.isEmpty {
            row(title) {
                ForEach(people) { person in
                    PersonCard(name: person.name, detail: person.bookCount == 1 ? "1 book" : "\(person.bookCount) books", image: person.image) {
                        router.push(.person(id: person.id, name: person.name))
                    }
                }
            }
        }
    }

    private func row(_ title: String, @ViewBuilder content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            RowHeading(title: title)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) { content() }
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
            shelves = try await client.bookShelves(library.id)
            error = nil
            loadedAt = Date()
            loadedToken = token
        } catch {
            if shelves == nil { self.error = ConnectionError(error).message }
        }
    }
}

/// A series: its first book's cover with the name and how many books beneath.
struct SeriesCard: View {
    @Environment(AppModel.self) private var model
    let series: BookSeriesSummary
    let action: () -> Void

    var body: some View {
        ArtworkButton(ambient: .silver, action: action) {
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
            .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
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
                .shadow(color: .black.opacity(0.5), radius: 30, y: 18)
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
                    .buttonStyle(ActionButtonStyle(ambient: item.glowColor, prominent: true))
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
                        Button {
                            Task {
                                await model.setBookFinished(item, !item.userData.watched)
                                await load()
                            }
                        } label: {
                            Label(item.userData.watched ? "Mark as not finished" : "Mark as finished",
                                  systemImage: item.userData.watched ? "arrow.uturn.backward" : "checkmark")
                        }
                        .buttonStyle(ActionButtonStyle(iconOnly: true))
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
            }
            .scrollClipDisabled()
            .focusSection()
        } else {
            let read = item.book?.narrator.map { "Read by \($0)" }
            Text([item.bookAuthors, read].compactMap { $0 }.joined(separator: "   "))
                .font(.system(size: 28, weight: .medium))
                .foregroundStyle(Color.usher)
                .lineLimit(2)
        }
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
