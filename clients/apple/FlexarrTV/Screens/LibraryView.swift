import FlexarrKit
import SwiftUI

/// Poster grid for one library, paged with skip/take, with sort and genre filters, and an A–Z rail to jump
/// through it when sorted by title.
struct LibraryView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let library: Library
    var initialSort: LibrarySort = .title
    var initialGenre: String?
    /// On a library's tab, the Browse and All switch; none when opened from a row's "See all".
    var viewSwitch: Binding<LibraryOpening>?

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
    @State private var loadedAt: Date?
    @State private var isVisible = false
    /// Bumped by each reload (a new sort or genre), so an answer for the old one is dropped rather than shown or appended.
    @State private var generation = 0
    @State private var isRefreshing = false
    /// The letter being jumped to while the titles up to it load.
    @State private var jumping: String?
    @FocusState private var focusedItem: String?
    /// The sort and genre buttons and the A–Z rail.
    @FocusState private var focusedControl: String?
    @State private var switchFocused = false
    /// When focus last left something on this screen (or the screen appeared), to tell focus coming down from the
    /// tab bar from focus moving within the screen or coming back to it.
    @State private var lastFocusHereAt = Date.distantPast
    @Environment(\.scenePhase) private var scenePhase

    private static let pageSize = 60
    /// The most the server sends in one page (ItemEndpoints' MaxPageSize).
    private static let maxPageSize = 500
    static let letters = ["#"] + "ABCDEFGHIJKLMNOPQRSTUVWXYZ".map(String.init)
    /// Room on the right for the A–Z rail; the six posters close up a little to make it.
    private static let railGutter: CGFloat = 170

    /// The rail only makes sense when the titles are in A–Z order.
    private var showsLetters: Bool { sort == .title && total > Self.pageSize }

    private var columns: [GridItem] {
        let trailing = showsLetters ? Self.railGutter : Layout.edge
        let spacing = (1920 - Layout.edge - trailing - 6 * Layout.posterWidth) / 5
        return Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: spacing, alignment: .top), count: 6)
    }

    var body: some View {
        ScrollViewReader { proxy in
            grid
                .overlay(alignment: .trailing) {
                    if showsLetters {
                        LetterRail(letters: Self.letters, empty: emptyLetters, jumping: jumping, focus: $focusedControl) { letter in
                            Task { await jump(to: letter, proxy: proxy) }
                        }
                        .padding(.trailing, Layout.edge)
                        .padding(.top, viewSwitch == nil ? 0 : 60)
                    }
                }
        }
    }

    private var grid: some View {
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
                            PosterCard(item: item, focus: $focusedItem) { router.push(.item(item)) }
                                .id(item.id)
                                .onAppear { loadMoreIfNeeded(at: index) }
                        }
                    }
                    .focusSection()
                    if isLoading {
                        ProgressView().frame(maxWidth: .infinity).padding(40)
                    }
                }
            }
            .padding(.leading, Layout.edge)
            .padding(.trailing, showsLetters ? Self.railGutter : Layout.edge)
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        // On a tab, the switch sits where it does over Browse's hero.
        .ignoresSafeArea(edges: viewSwitch == nil ? [] : .top)
        .background(RoomBackground(ambient: items.first?.glowColor, intensity: 0.18))
        .task {
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
            async let genresTask = try? model.client?.libraryGenres(library.id)
            await reload()
            genres = await genresTask ?? []
        }
        .onAppear {
            isVisible = true
            // Back from a title, focus returns to the poster that was opened: leave it there.
            lastFocusHereAt = .now
        }
        .onChange(of: focusedControl) { _, now in if now == nil { lastFocusHereAt = .now } }
        .onChange(of: focusedItem) { old, new in
            if old != nil, new == nil { lastFocusHereAt = .now }
            focusFirstTitleFromTabBar(old: old, new: new)
        }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible, Freshness.isStale(loadedAt) { Task { await refreshLoaded() } }
        }
        .onChange(of: sort) { Task { await reload() } }
        .onChange(of: genre) { Task { await reload() } }
        .onChange(of: model.refreshToken) { Task { await refreshLoaded() } }
    }

    private var header: some View {
        HStack(alignment: .lastTextBaseline, spacing: 30) {
            VStack(alignment: .leading, spacing: viewSwitch == nil ? 4 : 16) {
                if let viewSwitch {
                    // On a tab, which names the library: the Browse and All switch, where it sits over Browse's hero.
                    LibraryViewSwitch(library: library, view: viewSwitch) { focused in
                        if !focused { lastFocusHereAt = .now }
                        switchFocused = focused
                    }
                    .focusSection()
                } else {
                    Text(library.name)
                        .font(.marquee(76, weight: .heavy))
                        .foregroundStyle(Color.screen)
                }
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
                        .focused($focusedControl, equals: "sort-\(option.rawValue)")
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
                    .focused($focusedControl, equals: "genre")
                    .buttonStyle(PillButtonStyle(selected: genre != nil))
                }
            }
            .focusSection()
        }
        .padding(.top, viewSwitch == nil ? 20 : LibraryViewSwitch.topInset)
    }

    private func reload() async {
        guard let client = model.client else { return }
        generation += 1
        let current = generation
        isLoading = true
        // A newer reload owns the spinner by now.
        defer { if current == generation { isLoading = false } }
        let token = model.refreshToken
        do {
            let page = try await client.libraryItems(library.id, sort: sort, genre: genre, skip: 0, take: Self.pageSize)
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
    /// Only one runs at a time (playback ending and Back both ask within a second or two); one asked for while
    /// it runs because something changed again is run once it finishes.
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
    /// (a page failed, or more were appended meanwhile) is kept as it was, so focus and scroll stay put.
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
            guard let page = try? await client.libraryItems(
                library.id, sort: sort, genre: genre, skip: offset, take: min(wanted - offset, Self.maxPageSize)),
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

    /// Down from the tab bar, focus lands on whichever poster in the top row is under the tab it left; the first
    /// title is where a page starts, so it goes there instead. Focus moving here from the switch, the sort buttons or
    /// the rail, a letter jump, or coming back from a title, stays where it lands.
    private func focusFirstTitleFromTabBar(old: String?, new: String?) {
        guard old == nil, let new, jumping == nil, focusedControl == nil, !switchFocused,
              Date.now.timeIntervalSince(lastFocusHereAt) > 1,
              let index = items.firstIndex(where: { $0.id == new }), index > 0, index < 6,
              let first = items.first else { return }
        focusedItem = first.id
    }

    /// A title's letter on the A–Z rail, ignoring a leading "The", "A" or "An" as the server's sort does.
    static func letter(of item: ItemSummary) -> String {
        var title = Substring(item.title)
        for article in ["the ", "a ", "an "] where title.lowercased().hasPrefix(article) {
            title = title.dropFirst(article.count)
            break
        }
        guard let first = title.first?.uppercased(), first.count == 1, ("A"..."Z").contains(first) else { return "#" }
        return first
    }

    /// Letters with nothing to jump to: none among the titles loaded, and either every title is loaded or the grid
    /// has already loaded past the letter. "#" also covers accented titles, which sort after Z, so it's only known
    /// once everything is loaded.
    private var emptyLetters: Set<String> {
        let found = Set(items.map(Self.letter(of:)))
        let complete = items.count >= total
        let last = items.last.map(Self.letter(of:)) ?? "#"
        return Set(Self.letters.filter { letter in
            !found.contains(letter) && (complete || (letter != "#" && last != "#" && last > letter))
        })
    }

    /// Loads titles until the first one under `letter` is in the grid, then scrolls to it and gives it focus.
    private func jump(to letter: String, proxy: ScrollViewProxy) async {
        guard let client = model.client, jumping == nil else { return }
        let current = generation
        jumping = letter
        isLoading = true
        defer {
            jumping = nil
            if current == generation { isLoading = false }
        }
        while current == generation {
            if let target = items.first(where: { Self.letter(of: $0) == letter }) {
                // Titles just added aren't laid out yet; give the grid a moment, then once more for focus, since a
                // card off screen can't take it until the scroll has brought it in.
                await Task.yield()
                proxy.scrollTo(target.id, anchor: .top)
                try? await Task.sleep(for: .milliseconds(150))
                proxy.scrollTo(target.id, anchor: .top)
                focusedItem = target.id
                return
            }
            guard !emptyLetters.contains(letter), items.count < total else { return }
            let skip = items.count
            guard let page = try? await client.libraryItems(library.id, sort: sort, genre: genre, skip: skip, take: Self.maxPageSize),
                  current == generation, !page.items.isEmpty else { return }
            // A page from scrolling landed meanwhile: this one no longer follows on, so look again from there.
            guard skip == items.count else { continue }
            let known = Set(items.map(\.id))
            items.append(contentsOf: page.items.filter { !known.contains($0.id) })
            total = page.total
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
            if let page = try? await client.libraryItems(library.id, sort: sort, genre: genre, skip: skip, take: Self.pageSize),
               current == generation, skip == items.count {
                let known = Set(items.map(\.id))
                items.append(contentsOf: page.items.filter { !known.contains($0.id) })
                total = page.total
            }
        }
    }
}

/// The A–Z rail down the right of a library's grid. Right from the last column reaches it; a letter with nothing
/// under it can't be picked.
private struct LetterRail: View {
    let letters: [String]
    let empty: Set<String>
    let jumping: String?
    var focus: FocusState<String?>.Binding
    let jump: (String) -> Void

    var body: some View {
        VStack(spacing: 0) {
            ForEach(letters, id: \.self) { letter in
                Button { jump(letter) } label: {
                    Text(letter)
                }
                .buttonStyle(LetterButtonStyle(jumping: jumping == letter))
                .disabled(empty.contains(letter))
                .focused(focus, equals: "letter-\(letter)")
                .accessibilityLabel(letter == "#" ? "Numbers and symbols" : letter)
            }
        }
        .focusSection()
    }
}

private struct LetterButtonStyle: ButtonStyle {
    var jumping: Bool

    func makeBody(configuration: Configuration) -> some View {
        LetterBody(configuration: configuration, jumping: jumping)
    }

    private struct LetterBody: View {
        let configuration: Configuration
        let jumping: Bool
        @Environment(\.isFocused) private var isFocused
        @Environment(\.isEnabled) private var isEnabled

        var body: some View {
            configuration.label
                .font(.system(size: 22, weight: isFocused ? .heavy : .semibold))
                .foregroundStyle(isFocused ? Color.room : (isEnabled ? Color.screen.opacity(0.8) : Color.usher.opacity(0.35)))
                .frame(width: 54, height: 31)
                .background {
                    Capsule().fill(isFocused ? Color.screen : .clear)
                }
                .overlay {
                    if jumping { ProgressView().scaleEffect(0.6).offset(x: -54) }
                }
                .scaleEffect(isFocused ? 1.3 : 1)
                .animation(.spring(response: 0.25, dampingFraction: 0.8), value: isFocused)
        }
    }
}
