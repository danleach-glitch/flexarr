import FlexarrKit
import SwiftUI

/// A book's page: its cover, title, series and people, Play, then its chapters, bookmarks and description, and more
/// in the series, by the author and read by the narrator.
struct BookView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.scenePhase) private var scenePhase
    let id: String
    let preview: ItemSummary?

    @State private var detail: ItemDetail?
    @State private var error: String?
    @State private var loadedToken: Int?
    @State private var loadedAt: Date?
    @State private var isVisible = false
    @State private var overviewExpanded = false
    @State private var allChapters = false
    /// Start over was tapped part way through: it asks first.
    @State private var confirmingStartOver = false
    /// What the Bookmark button came to, for a moment.
    @State private var notice: BookmarkNotice?
    /// The bookmark whose note is being written.
    @State private var editing: Bookmark?
    @State private var note = ""

    private var item: ItemSummary? { detail?.item ?? preview }
    private var book: BookDetail? { detail?.book }
    private var ambient: Color { item?.glowColor ?? .silver }

    /// Where this profile is: the player's place while it's playing this book, else the server's.
    private var position: Double {
        if model.music.current?.id == id { return model.music.position }
        return item?.userData.positionSeconds ?? 0
    }

    private var duration: Double { book?.durationSeconds ?? item?.book?.durationSeconds ?? item?.effectiveDuration ?? 0 }
    private var speed: Double { book?.speed ?? model.bookListening?.speed ?? 1 }

    var body: some View {
        ZStack {
            RoomBackground(ambient: ambient, intensity: 0.3)
            if let item {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 28) {
                        header(item)
                        sections(item)
                    }
                    .padding(.top, 8)
                    .padding(.bottom, 40)
                    .frame(maxWidth: 720)
                    .frame(maxWidth: .infinity)
                }
                .refreshable { await load() }
            } else if let error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .overlay(alignment: .top) {
            if let notice { NoticeCapsule(notice: notice).padding(.top, 8) }
        }
        .animation(.easeInOut(duration: 0.25), value: notice)
        .alert("Note", isPresented: Binding(get: { editing != nil }, set: { if !$0 { editing = nil } })) {
            TextField("Note", text: $note)
            Button("Save") { if let editing { save(note, for: editing) } }
            Button("Cancel", role: .cancel) {}
        }
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

    // MARK: Header

    private func header(_ item: ItemSummary) -> some View {
        VStack(spacing: 16) {
            BookCover(item: item, imageWidth: 800)
                .frame(maxWidth: sizeClass == .regular ? 320 : 240)
                .shadow(color: .black.opacity(0.5), radius: 24, y: 12)
                .shadow(color: ambient.opacity(0.35), radius: 40)
            VStack(spacing: 6) {
                Text(item.title)
                    .font(.marquee(30, weight: .black))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                if let subtitle = book?.subtitle {
                    Text(subtitle)
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(Color.screen.opacity(0.8))
                        .multilineTextAlignment(.center)
                }
                if let series = book?.series.first {
                    Button { router.push(.bookSeries(id: series.id, name: series.name)) } label: {
                        Text([series.name, series.placeLabel].compactMap { $0 }.joined(separator: ", "))
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(Color.silver)
                    }
                    .buttonStyle(.plain)
                    .accessibilityHint("Opens the series")
                }
                people
                FactsLine(facts: facts(item))
                    .font(.caption)
                    .padding(.top, 2)
            }
            .padding(.horizontal, Layout.edge)
            actions(item)
                .padding(.horizontal, Layout.edge)
        }
        .frame(maxWidth: .infinity)
    }

    /// The authors, then "Read by" the narrators: each name opens their page.
    @ViewBuilder
    private var people: some View {
        let credits = detail?.credits ?? []
        let authors = credits.filter { $0.role == .author }
        let narrators = credits.filter { $0.role == .narrator }
        if !authors.isEmpty || item?.bookAuthors != nil {
            VStack(spacing: 2) {
                if authors.isEmpty {
                    Text(item?.bookAuthors ?? "").foregroundStyle(Color.screen.opacity(0.9))
                } else {
                    names(authors)
                }
                if !narrators.isEmpty {
                    HStack(spacing: 4) {
                        Text("Read by").foregroundStyle(Color.usher)
                        names(narrators)
                    }
                } else if let narrator = item?.book?.narrator {
                    Text("Read by \(narrator)").foregroundStyle(Color.usher)
                }
            }
            .font(.subheadline.weight(.medium))
            .lineLimit(1)
        }
    }

    private func names(_ credits: [Credit]) -> some View {
        HStack(spacing: 0) {
            ForEach(Array(credits.prefix(3).enumerated()), id: \.offset) { index, credit in
                if index > 0 { Text(", ").foregroundStyle(Color.usher) }
                Button(credit.name) { router.push(.person(id: credit.personId, name: credit.name)) }
                    .buttonStyle(.plain)
                    .foregroundStyle(Color.screen.opacity(0.9))
            }
        }
    }

    /// Its length, and how long is left at this profile's speed for it.
    private func facts(_ item: ItemSummary) -> [String] {
        var facts: [String] = []
        if let length = Format.runtime(seconds: duration) { facts.append(length) }
        if position > 0, !item.isFinishedBook, let left = Format.remaining((duration - position) / max(0.5, speed)) {
            facts.append(speed == 1 ? left : "\(left) at \(BookSpeed.label(speed))")
        }
        if item.isFinishedBook { facts.append("Finished") }
        return facts
    }

    private func actions(_ item: ItemSummary) -> some View {
        let resumable = position > 0 && !item.isFinishedBook
        let playingThis = model.music.current?.id == item.id
        return VStack(spacing: 18) {
            Button {
                if playingThis { model.music.togglePlayPause() } else { model.playBook(item) }
            } label: {
                Label(playingThis && model.music.isPlaying ? "Pause" : resumable ? "Resume from \(Format.timestamp(position))" : "Play",
                      systemImage: playingThis && model.music.isPlaying ? "pause.fill" : "play.fill")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(ActionButtonStyle(ambient: ambient, prominent: true))
            .monospacedDigit()

            HStack(alignment: .top, spacing: 8) {
                BookDownloadButton(item: item)
                if !model.isKids, !item.isFinishedBook {
                    Button { Task { await setFinished(true) } } label: { Label("Finished", systemImage: "checkmark") }
                        .accessibilityLabel("Mark as finished")
                }
                // Unread (no place, not finished, as if never started) in Finished's place once it's finished. Part way through,
                // it's offered with Start over instead, so the row stays at four.
                if !model.isKids, item.isFinishedBook {
                    Button { Task { await setFinished(false) } } label: { Label("Unread", systemImage: "book.closed") }
                        .accessibilityLabel("Mark as unread")
                }
                // Only with somewhere to start over from; part way through, it asks first.
                if item.isFinishedBook || resumable {
                    Button {
                        if resumable { confirmingStartOver = true } else { Task { await model.listenAgain(item) } }
                    } label: {
                        Label("Start over", systemImage: "arrow.counterclockwise")
                    }
                    .confirmationDialog("Start over?", isPresented: $confirmingStartOver, titleVisibility: .visible) {
                        Button("Start over", role: .destructive) { Task { await model.listenAgain(item) } }
                        // Not while it plays, which would set the place again.
                        if !model.isKids, !playingThis {
                            Button("Mark as unread") { Task { await setFinished(false) } }
                        }
                        Button("Cancel", role: .cancel) {}
                    } message: {
                        Text("You're at \(Format.timestamp(position)).")
                    }
                }
                Button { addBookmark() } label: { Label("Bookmark", systemImage: "bookmark") }
                    .accessibilityLabel("Add a bookmark here")
            }
            .buttonStyle(LabeledActionStyle())
        }
        .frame(maxWidth: 520)
    }

    // MARK: Sections

    @ViewBuilder
    private func sections(_ item: ItemSummary) -> some View {
        if let overview = detail?.overview, !overview.isEmpty {
            Text(overview)
                .font(.subheadline)
                .lineSpacing(3)
                .foregroundStyle(Color.screen.opacity(0.88))
                .lineLimit(overviewExpanded ? nil : 5)
                .padding(.horizontal, Layout.edge)
                .contentShape(Rectangle())
                .onTapGesture { withAnimation(.easeInOut(duration: 0.2)) { overviewExpanded.toggle() } }
                .accessibilityAddTraits(.isButton)
                .accessibilityHint(overviewExpanded ? "Shows less" : "Shows the whole description")
        }
        if let authors = book?.authorCards, !authors.isEmpty {
            VStack(spacing: 10) {
                ForEach(authors) { authorCard($0) }
            }
            .padding(.horizontal, Layout.edge)
        }
        if let book {
            if book.chapters.count > 1 { chapters(book.chapters, item: item) }
            if !book.bookmarks.isEmpty { bookmarks(book.bookmarks, chapters: book.chapters, item: item) }
            bookRow("More in the series", book.moreInSeries)
            bookRow(firstName(.author).map { "More by \($0)" } ?? "More by the author", book.byAuthor)
            bookRow(firstName(.narrator).map { "More read by \($0)" } ?? "More from the narrator", book.readByNarrator)
            details(book)
        }
    }

    /// An author's photo, name and the start of their biography: tapping opens their page.
    private func authorCard(_ author: BookAuthor) -> some View {
        Button { router.push(.person(id: author.id, name: author.name)) } label: {
            HStack(alignment: .top, spacing: 14) {
                Color.seat
                    .frame(width: 56, height: 56)
                    .overlay {
                        RemoteImage(url: model.client?.imageURL(author.image, width: 168)) {
                            Image(systemName: "person.fill").font(.title3).foregroundStyle(Color.usher)
                        }
                    }
                    .clipShape(Circle())
                VStack(alignment: .leading, spacing: 4) {
                    Text(author.name)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Color.screen)
                    if let biography = author.biography?.trimmingCharacters(in: .whitespacesAndNewlines), !biography.isEmpty {
                        Text(biography)
                            .font(.footnote)
                            .lineSpacing(2)
                            .foregroundStyle(Color.screen.opacity(0.75))
                            .lineLimit(3)
                    }
                }
                .multilineTextAlignment(.leading)
                Spacer(minLength: 0)
            }
            .padding(14)
            .background(Color.seat.opacity(0.6), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            .contentShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(author.name)
        .accessibilityAddTraits(.isButton)
        .accessibilityHint("Opens their page")
    }

    private func firstName(_ role: CreditRole) -> String? {
        detail?.credits.first { $0.role == role }?.name
    }

    /// The chapters: those heard dimmed, the one playing with how far in it is. Long lists show the ones around the
    /// current one until All chapters is tapped.
    private func chapters(_ chapters: [BookChapter], item: ItemSummary) -> some View {
        let timeline = BookTimeline(files: [], chapters: chapters, duration: duration)
        let current = position > 0 && !item.isFinishedBook ? timeline.chapterIndex(at: position) : nil
        let shown: [BookChapter] = {
            guard !allChapters, chapters.count > 8 else { return chapters }
            let start = max(0, min((current ?? 0) - 2, chapters.count - 8))
            return Array(chapters[start..<min(chapters.count, start + 8)])
        }()
        return VStack(alignment: .leading, spacing: 4) {
            SectionHeading(title: "Chapters", actionTitle: allChapters ? "Fewer" : "All \(chapters.count)",
                           action: chapters.count > 8 ? { withAnimation { allChapters.toggle() } } : nil)
            ForEach(shown) { chapter in
                let index = chapters.firstIndex(of: chapter) ?? 0
                Button { model.playBook(item, at: chapter.startSeconds) } label: {
                    HStack(spacing: 12) {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(chapter.title)
                                .font(.subheadline.weight(index == current ? .bold : .regular))
                                .foregroundStyle(index == current ? Color.screen : Color.screen.opacity((current ?? -1) > index || item.isFinishedBook ? 0.5 : 0.85))
                                .multilineTextAlignment(.leading)
                            if index == current, chapter.durationSeconds > 0 {
                                ProgressBar(value: min(1, max(0, (position - chapter.startSeconds) / chapter.durationSeconds)), tint: .silver)
                                    .frame(maxWidth: 200)
                            }
                        }
                        Spacer(minLength: 8)
                        if (current ?? -1) > index || item.isFinishedBook {
                            Image(systemName: "checkmark").font(.caption.weight(.bold)).foregroundStyle(Color.usher)
                        }
                        Text(Format.timestamp(chapter.durationSeconds))
                            .font(.caption.weight(.medium))
                            .monospacedDigit()
                            .foregroundStyle(Color.usher)
                    }
                    .padding(.horizontal, Layout.edge)
                    .padding(.vertical, 9)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("\(chapter.title), \(Format.timestamp(chapter.durationSeconds))")
                .accessibilityAddTraits(index == current ? .isSelected : [])
            }
        }
    }

    /// Bookmarks: each plays from its place; a long press writes its note or deletes it.
    private func bookmarks(_ marks: [Bookmark], chapters: [BookChapter], item: ItemSummary) -> some View {
        let timeline = BookTimeline(files: [], chapters: chapters, duration: duration)
        return VStack(alignment: .leading, spacing: 4) {
            SectionHeading(title: "Bookmarks")
            ForEach(marks.sorted { $0.positionSeconds < $1.positionSeconds }) { mark in
                Button { model.playBook(item, at: mark.positionSeconds) } label: {
                    HStack(alignment: .firstTextBaseline, spacing: 12) {
                        Text(Format.timestamp(mark.positionSeconds))
                            .font(.caption.weight(.semibold))
                            .monospacedDigit()
                            .foregroundStyle(Color.usher)
                            .frame(minWidth: 52, alignment: .leading)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(mark.note ?? timeline.chapter(at: mark.positionSeconds)?.title ?? "Bookmark")
                                .font(.subheadline)
                                .foregroundStyle(Color.screen)
                                .multilineTextAlignment(.leading)
                            if mark.note != nil, let chapter = timeline.chapter(at: mark.positionSeconds) {
                                Text(chapter.title).font(.caption).foregroundStyle(Color.usher)
                            }
                        }
                        Spacer(minLength: 0)
                    }
                    .padding(.horizontal, Layout.edge)
                    .padding(.vertical, 8)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .contextMenu {
                    Button { note = mark.note ?? ""; editing = mark } label: { Label("Edit note", systemImage: "pencil") }
                    Button(role: .destructive) { delete(mark) } label: { Label("Delete", systemImage: "trash") }
                }
            }
        }
    }

    @ViewBuilder
    private func bookRow(_ title: String, _ items: [ItemSummary]) -> some View {
        if !items.isEmpty {
            VStack(alignment: .leading, spacing: 10) {
                SectionHeading(title: title)
                ScrollView(.horizontal) {
                    LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                        ForEach(items) { BookCard(item: $0, width: Layout.posterWidth(sizeClass) * 1.15) }
                    }
                    .padding(.horizontal, Layout.edge)
                }
                .scrollIndicators(.hidden)
            }
        }
    }

    private func details(_ book: BookDetail) -> some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), alignment: .topLeading)], alignment: .leading, spacing: 14) {
            if let publisher = book.publisher { labeled("Publisher", publisher) }
            if let year = item?.releaseYear { labeled("Released", String(year)) }
            if let language = book.language { labeled("Language", language) }
            if book.abridged { labeled("Edition", "Abridged") }
            if let genres = detail?.genres, !genres.isEmpty { labeled("Genre", genres.prefix(2).joined(separator: ", ")) }
        }
        .padding(.horizontal, Layout.edge)
    }

    private func labeled(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption.weight(.semibold)).foregroundStyle(Color.usher)
            Text(value).font(.subheadline).foregroundStyle(Color.screen.opacity(0.85))
        }
    }

    // MARK: Changes

    private func setFinished(_ finished: Bool) async {
        guard let item else { return }
        await model.setBookFinished(item, finished)
        await load()
    }

    /// A bookmark where the player is (playing this book) or where this profile got to, with a moment's notice.
    private func addBookmark() {
        let place = position
        if model.music.current?.id == id, model.music.book != nil {
            Task {
                let added = await model.music.addBookmark()
                show(added != nil ? .added(place) : model.music.bookmarkNotice ?? BookmarkNotice(text: "Bookmark not added", isError: true))
                await load()
            }
            return
        }
        guard let client = model.client else { return }
        Task {
            do {
                _ = try await client.addBookmark(id, positionSeconds: place)
                show(.added(place))
            } catch {
                show(.failed(error))
            }
            await load()
        }
    }

    private func show(_ shown: BookmarkNotice) {
        if model.music.current?.id != id { shown.tap() } // the player taps for its own
        notice = shown
        Task {
            try? await Task.sleep(for: .seconds(shown.isError ? 4 : 2))
            if notice == shown { notice = nil }
        }
    }

    private func save(_ note: String, for mark: Bookmark) {
        guard let client = model.client else { return }
        Task {
            _ = try? await client.setBookmarkNote(mark.id, note: note.isEmpty ? nil : note)
            await load()
        }
    }

    private func delete(_ mark: Bookmark) {
        guard let client = model.client else { return }
        Task {
            try? await client.deleteBookmark(mark.id)
            await load()
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        let token = model.refreshToken
        do {
            detail = try await client.item(id)
            loadedToken = token
            loadedAt = Date()
            error = nil
        } catch {
            if detail == nil { self.error = ConnectionError(error).message }
        }
    }
}
