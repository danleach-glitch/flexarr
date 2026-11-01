import FlexarrKit
import SwiftUI

struct SearchView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(Router.self) private var router
    @State private var query = ""
    @FocusState private var typing: Bool
    @State private var results: SearchResults?
    @State private var error: String?
    @State private var recents = RecentSearches()
    /// Films and shows beyond the library (through Seerr), to add to the watchlist or request.
    @State private var outside: [ExternalTitle] = []
    /// Seerr is set up, so searches look beyond the library too; nil until asked.
    @State private var looksOutside: Bool?

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 24) {
                if let results {
                    if results.isEmpty && outside.isEmpty {
                        StatusView(state: .empty("Nothing matches \u{201C}\(query)\u{201D}."))
                            .frame(height: 240)
                    }
                    if !results.titles.isEmpty {
                        section("Movies and shows") {
                            ForEach(results.titles) { item in
                                PosterCard(item: item, width: Layout.posterWidth(sizeClass)) {
                                    model.recordSearch(query: query, item: item)
                                }
                            }
                        }
                    }
                    if let books = results.books, !books.isEmpty {
                        section("Audiobooks") {
                            ForEach(books) { item in
                                BookCard(item: item, width: Layout.posterWidth(sizeClass) * 1.15) {
                                    model.recordSearch(query: query, item: item)
                                }
                            }
                        }
                    }
                    if let series = results.bookSeries, !series.isEmpty {
                        section("Series") {
                            ForEach(series) { found in
                                Button {
                                    model.recordSearch(query: query)
                                    router.push(.bookSeries(id: found.id, name: found.name))
                                } label: {
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(found.name).font(.subheadline.weight(.semibold)).foregroundStyle(Color.screen)
                                        Text(found.bookCount == 1 ? "1 book" : "\(found.bookCount) books").font(.caption).foregroundStyle(Color.usher)
                                    }
                                    .lineLimit(1)
                                    .padding(.horizontal, 16)
                                    .padding(.vertical, 12)
                                    .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.seat))
                                }
                                .buttonStyle(CardButtonStyle())
                            }
                        }
                    }
                    if !results.episodes.isEmpty {
                        section("Episodes") {
                            ForEach(results.episodes) { item in
                                WideCard(item: item, width: Layout.wideWidth(sizeClass)) {
                                    model.recordSearch(query: query, item: item)
                                    model.play(item)
                                }
                            }
                        }
                    }
                    if !outside.isEmpty {
                        section("Not in your library") {
                            ForEach(outside) { title in
                                OutsideTitleCard(title: title, width: Layout.posterWidth(sizeClass))
                            }
                        }
                    }
                    if !results.people.isEmpty {
                        section("People") {
                            ForEach(results.people) { person in
                                PersonCard(id: person.id, name: person.name,
                                           detail: person.credits == 1 ? "1 title" : "\(person.credits) titles", image: person.image) {
                                    model.recordSearch(query: query)
                                }
                            }
                        }
                    }
                } else if let error {
                    StatusView(state: .failed(error) { Task { await search() } })
                        .frame(height: 240)
                } else if query.isEmpty && !recents.isEmpty {
                    recentSections
                } else {
                    StatusView(state: .empty("Find films, shows, episodes and people in your libraries."))
                        .frame(height: 240)
                }
            }
            .padding(.vertical, 12)
        }
        .scrollDismissesKeyboard(.immediately)
        .background(RoomBackground(ambient: nil))
        .navigationTitle("Search")
        // Always showing: left to itself, iOS tucks the field away until the page is pulled down.
        .searchable(text: $query, placement: .navigationBarDrawer(displayMode: .always), prompt: "Movies, shows, episodes and people")
        // Opening Search is for typing: the field's ready with the keyboard up.
        .searchFocused($typing)
        // Pressing Search keeps the words as a recent search, as picking a result does: shown at once, without waiting
        // for the server, so clearing the field shows it straight away.
        .onSubmit(of: .search) {
            let words = query.trimmingCharacters(in: .whitespaces)
            guard !words.isEmpty, model.player.recordsHistory else { return }
            model.recordSearch(query: words)
            recents = RecentSearches(queries: Array(([words] + recents.queries.filter { $0.caseInsensitiveCompare(words) != .orderedSame }).prefix(5)), items: recents.items)
        }
        .onAppear {
            takeSiriSearch()
            if query.isEmpty { typing = true }
        }
        .onChange(of: model.pendingSearch) { takeSiriSearch() }
        .task(id: query) {
            // Debounce typing so each keystroke doesn't hit the server.
            try? await Task.sleep(for: .milliseconds(300))
            guard !Task.isCancelled else { return }
            await search()
        }
    }

    /// With nothing typed: the profile's last few searches to run again, and the titles opened from search.
    @ViewBuilder private var recentSections: some View {
        if !recents.queries.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                SectionHeading(title: "Recent searches", actionTitle: "Clear") { clearRecents() }
                VStack(spacing: 0) {
                    ForEach(recents.queries, id: \.self) { words in
                        recentQuery(words)
                    }
                }
                .padding(.horizontal, Layout.edge)
            }
        }
        if !recents.items.isEmpty {
            // Clear sits on the first heading shown.
            let clear: (() -> Void)? = recents.queries.isEmpty ? { clearRecents() } : nil
            section("Recently opened", clear: clear) {
                ForEach(recents.items) { item in
                    PosterCard(item: item, width: Layout.posterWidth(sizeClass)) {
                        model.recordSearch(query: nil, item: item)
                    }
                }
            }
        }
    }

    private func recentQuery(_ words: String) -> some View {
        HStack(spacing: 12) {
            Button {
                query = words
                typing = false
            } label: {
                HStack(spacing: 12) {
                    Image(systemName: "clock.arrow.circlepath").foregroundStyle(Color.usher)
                    Text(words).foregroundStyle(Color.screen).lineLimit(1)
                    Spacer(minLength: 0)
                }
                .padding(.vertical, 10)
                .contentShape(Rectangle())
            }
            Button { forget(words) } label: {
                Image(systemName: "xmark")
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Color.usher)
                    .frame(width: 32, height: 32)
                    .contentShape(Rectangle())
            }
            .accessibilityLabel("Remove \(words)")
        }
        .buttonStyle(.plain)
        .contextMenu {
            Button(role: .destructive) { forget(words) } label: { Label("Remove", systemImage: "xmark") }
        }
    }

    private func section(_ title: String, clear: (() -> Void)? = nil, @ViewBuilder content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: title, actionTitle: "Clear", action: clear)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) { content() }
                    .padding(.horizontal, Layout.edge)
            }
            .scrollIndicators(.hidden)
        }
    }

    /// "Search for Fargo in Flexarr" said to Siri: the words go in the field and the search runs.
    private func takeSiriSearch() {
        guard let words = model.pendingSearch else { return }
        model.pendingSearch = nil
        query = words
        typing = false
    }

    private func search() async {
        let text = query.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty, let client = model.client else {
            results = nil
            error = nil
            await loadRecents()
            return
        }
        if looksOutside == nil {
            // Asked once, before the first search that needs it.
            looksOutside = (try? await client.requestStatus())?.configured == true
        }
        do {
            async let beyond: [ExternalTitle] = looksOutside == true ? (try? await client.searchEverything(text)) ?? [] : []
            let found = try await client.search(text)
            let more = await beyond
            guard text == query.trimmingCharacters(in: .whitespaces) else { return }
            results = found
            // What the library has is already above.
            outside = more.filter { $0.libraryItemId == nil && $0.availability != .available }
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    /// Servers before 0.56 keep no recent searches, and one that can't be reached shows none: nothing to say either way.
    private func loadRecents() async {
        guard let client = model.client else { return }
        recents = (try? await client.recentSearches()) ?? RecentSearches()
    }

    private func forget(_ words: String) {
        recents = RecentSearches(queries: recents.queries.filter { $0 != words }, items: recents.items)
        guard let client = model.client else { return }
        Task { try? await client.forgetRecentSearch(query: words) }
    }

    private func clearRecents() {
        recents = RecentSearches()
        guard let client = model.client else { return }
        Task { try? await client.clearRecentSearches() }
    }
}

struct PersonView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    let id: String
    let name: String
    @State private var person: Person?
    @State private var error: String?
    @State private var biographyExpanded = false

    private func unique(_ credits: [PersonCredit]) -> [PersonCredit] {
        var seen: Set<String> = []
        return credits.filter { seen.insert($0.item.id).inserted }
    }

    /// "12 titles in your library", or "3 books" for an author or narrator.
    private func countLine(_ person: Person) -> String {
        let count = unique(person.credits).count
        if !person.credits.isEmpty, person.credits.allSatisfy({ $0.item.kind == .audiobook }) {
            return count == 1 ? "1 book" : "\(count) books"
        }
        return count == 1 ? "1 title in your library" : "\(count) titles in your library"
    }

    private var columns: [GridItem] {
        let minimum: CGFloat = sizeClass == .regular ? 140 : 100
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.45), spacing: 12, alignment: .top)]
    }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 24) {
                HStack(spacing: 16) {
                    Color.seat
                        .frame(width: 88, height: 88)
                        .overlay { RemoteImage(url: model.client?.imageURL(person?.image, width: 240)) { Color.seat } }
                        .clipShape(Circle())
                    VStack(alignment: .leading, spacing: 4) {
                        Text(name).font(.marquee(34, weight: .heavy)).foregroundStyle(Color.screen)
                        if let person {
                            Text(countLine(person))
                                .font(.subheadline).foregroundStyle(Color.usher)
                        }
                    }
                }
                .padding(.horizontal, Layout.edge)
                if let person {
                    if let biography = person.biography, !biography.isEmpty {
                        Text(biography)
                            .font(.subheadline)
                            .lineSpacing(3)
                            .foregroundStyle(Color.screen.opacity(0.88))
                            .lineLimit(biographyExpanded ? nil : 5)
                            .padding(.horizontal, Layout.edge)
                            .contentShape(Rectangle())
                            .onTapGesture { withAnimation(.easeInOut(duration: 0.2)) { biographyExpanded.toggle() } }
                            .accessibilityAddTraits(.isButton)
                    }
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                        // An author's books and the ones they read are listed once each.
                        ForEach(unique(person.credits), id: \.item.id) { credit in
                            if credit.item.kind == .audiobook {
                                BookCard(item: credit.item)
                            } else {
                                PosterCard(item: credit.item)
                            }
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 300)
                } else {
                    ProgressView().frame(maxWidth: .infinity).padding(40)
                }
            }
            .padding(.vertical, 12)
        }
        .background(RoomBackground(ambient: person?.credits.first?.item.glowColor, intensity: 0.22))
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            person = try await client.person(id)
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}

/// A film or show the library doesn't have, from search: tap for Add to watchlist and Request.
private struct OutsideTitleCard: View {
    @Environment(AppModel.self) private var model
    let title: ExternalTitle
    let width: CGFloat
    @State private var onList: Bool?
    @State private var requested = false

    var body: some View {
        let isOn = onList ?? title.onWatchlist ?? false
        Menu {
            if model.hasWatchlist {
                Button {
                    Task { if await model.setOnWatchlist(WatchlistKey(title), !isOn) != nil { onList = !isOn } }
                } label: {
                    Label(isOn ? "Remove from watchlist" : "Add to watchlist", systemImage: isOn ? "bookmark.slash" : "bookmark")
                }
            }
            if title.availability == .none && !requested {
                Button {
                    Task {
                        do {
                            try await model.client?.request(title)
                            requested = true
                        } catch {
                            model.requestError = ConnectionError(error).message
                        }
                    }
                } label: {
                    Label("Request", systemImage: "plus.circle")
                }
            }
        } label: {
            VStack(alignment: .leading, spacing: 6) {
                Color.seat
                    .aspectRatio(2 / 3, contentMode: .fit)
                    .overlay {
                        RemoteImage(url: model.client?.imageURL(title.poster, width: 342)) {
                            Text(title.title)
                                .font(.marquee(18, weight: .bold, scales: false))
                                .foregroundStyle(Color.screen.opacity(0.8))
                                .multilineTextAlignment(.center)
                                .padding(8)
                        }
                    }
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                    .overlay(alignment: .topTrailing) {
                        if isOn {
                            Image(systemName: "bookmark.fill")
                                .font(.caption.weight(.bold))
                                .foregroundStyle(Color.screen)
                                .padding(6)
                                .background(Circle().fill(.black.opacity(0.55)))
                                .padding(6)
                        }
                    }
                VStack(alignment: .leading, spacing: 1) {
                    Text(title.title)
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                    Text([title.isShow ? "Show" : "Film", title.year.map(String.init), requested || title.availability == .requested ? "Requested" : nil]
                        .compactMap { $0 }.joined(separator: ", "))
                        .font(.caption)
                        .foregroundStyle(Color.usher)
                }
                .lineLimit(1)
            }
            .frame(width: width)
            .accessibilityElement(children: .combine)
        }
        .buttonStyle(.plain)
    }
}
