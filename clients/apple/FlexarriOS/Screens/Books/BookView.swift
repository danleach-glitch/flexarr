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
                if !model.isKids {
                    Button { Task { await setFinished(!item.isFinishedBook) } } label: {
                        Label(item.isFinishedBook ? "Not finished" : "Finished", systemImage: item.isFinishedBook ? "arrow.uturn.backward" : "checkmark")
                    }
                    .accessibilityLabel(item.isFinishedBook ? "Mark as not finished" : "Mark as finished")
                }
                if item.isFinishedBook || resumable {
                    Button { Task { await model.listenAgain(item) } } label: {
                        Label("Listen again", systemImage: "arrow.counterclockwise")
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
        if let book {
            if book.chapters.count > 1 { chapters(book.chapters, item: item) }
            if !book.bookmarks.isEmpty { bookmarks(book.bookmarks, chapters: book.chapters, item: item) }
            bookRow("More in the series", book.moreInSeries)
            bookRow(firstName(.author).map { "More by \($0)" } ?? "More by the author", book.byAuthor)
            bookRow(firstName(.narrator).map { "More read by \($0)" } ?? "More from the narrator", book.readByNarrator)
            details(book)
        }
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

    /// A bookmark where the player is (playing this book) or where this profile got to.
    private func addBookmark() {
        if model.music.current?.id == id, model.music.book != nil {
            Task {
                await model.music.addBookmark()
                await load()
            }
            return
        }
        guard let client = model.client else { return }
        Task {
            _ = try? await client.addBookmark(id, positionSeconds: position)
            await load()
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
