import FlexarrKit
import SwiftUI

/// The words at Discover's top-left, as a library's Browse and All are.
enum DiscoverMode: Hashable {
    /// What's trending and popular, and the library by genre.
    case browse
    /// Any film or show TMDB knows, to find something new and request it: opens the search full screen.
    case search
}

/// Discover: what's trending and popular (from Seerr, when it's connected), searching for any film or show to request
/// it, and the library by genre. As on the web.
struct DiscoverView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @State private var mode = DiscoverMode.browse
    @State private var status: RequestStatus?
    @State private var rows: [ExternalRow] = []
    @State private var genres: [Genre] = []
    @State private var loaded = false
    @State private var error: String?
    @State private var searching = false
    /// Focus has just come to the switch with the tab (see `focusesContent`).
    @State private var arriving = false
    @State private var switchFocused = false
    @FocusState private var focusedTitle: String?

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 50) {
                // Under the collapsed sidebar's label, where a library's Browse and All are; there from the start,
                // even while loading, so choosing Discover puts focus here and it can be passed on to the first title
                // (see `passFocusOnArrival`). Gone once it's known Seerr isn't connected. The width of the screen, so
                // Up from any title below comes to it.
                if status?.configured != false {
                    ViewSwitch(choices: [(.browse, "Browse"), (.search, "Search")], selection: $mode) { switchFocused = $0 }
                        .padding(.leading, Layout.edge)
                        .padding(.top, LibraryViewSwitch.topInset)
                        .padding(.bottom, -20)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .focusSection()
                }
                if !loaded, let error {
                    StatusView(state: .failed(error) { Task { await load() } })
                        .frame(height: 600)
                } else if !loaded {
                    StatusView(state: .loading).frame(height: 600)
                } else {
                    browse
                }
            }
            .padding(.top, status?.configured == false ? LibraryViewSwitch.topInset : 0)
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .ignoresSafeArea(edges: [.top, .horizontal])
        .background(RoomBackground(ambient: nil))
        .environment(\.focusesContent, $arriving)
        .onChange(of: arriving) { passFocusOnArrival() }
        .onChange(of: rows.first?.items.first?.id) { passFocusOnArrival() }
        // Straight to the keyboard: full screen, as the Search tab is, so the sidebar doesn't push it off the edge.
        .onChange(of: mode) { _, now in searching = now == .search }
        .fullScreenCover(isPresented: $searching, onDismiss: { mode = .browse }) {
            TabStack { DiscoverSearchView() }
                // Solid: a cover shows the screen under it otherwise, and until there are results the search screen
                // has nothing of its own behind the keyboard.
                .background(Color.room.ignoresSafeArea())
                .presentationBackground(Color.room)
        }
        // Again on coming back, so what was just requested says so.
        .task { await load() }
    }

    // MARK: Browse

    @ViewBuilder
    private var browse: some View {
        ForEach(rows.filter { !$0.items.isEmpty }) { row in
            DiscoverRow(title: row.title, titles: row.items, focus: $focusedTitle, focusPrefix: row.id)
        }
        if let note = seerrNote {
            Text(note)
                .font(.system(size: 27, weight: .medium))
                .foregroundStyle(Color.usher)
                .frame(maxWidth: 1200, alignment: .leading)
                .padding(.horizontal, Layout.edge)
                .padding(.top, 20)
        }
        if !genres.isEmpty {
            VStack(alignment: .leading, spacing: 30) {
                RowHeading(title: "Your library by genre")
                // Not lazy: the grid starts below the screen, and Down can only move to tiles that are there.
                // A library has a few dozen genres at most.
                Grid(alignment: .topLeading, horizontalSpacing: GenreTile.spacing, verticalSpacing: 44) {
                    ForEach(Array(stride(from: 0, to: genres.count, by: GenreTile.across)), id: \.self) { start in
                        GridRow {
                            ForEach(genres[start..<min(start + GenreTile.across, genres.count)]) { genre in
                                GenreTile(genre: genre) { router.push(.genre(genre.name)) }
                            }
                        }
                    }
                }
                .padding(.horizontal, Layout.edge)
                .focusSection()
            }
        }
    }

    /// Why there are no rows to request from: Seerr isn't connected, or isn't answering.
    private var seerrNote: String? {
        guard let status, rows.allSatisfy(\.items.isEmpty) else { return nil }
        if !status.configured {
            return "Connect Seerr under Settings › Connections on the web to find new films and shows here and request them."
        }
        return status.reachable ? nil : "Seerr isn’t answering, so there’s nothing new to request just now."
    }

    // MARK: Loading

    /// Choosing Discover in the sidebar gives focus to the switch at the top-left; pass it on to the first title, as a
    /// library's tab does.
    private func passFocusOnArrival() {
        guard arriving, switchFocused, let row = rows.first(where: { !$0.items.isEmpty }), let first = row.items.first else { return }
        focusedTitle = "\(row.id)/\(first.id)"
        arriving = false
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            async let status = client.requestStatus()
            async let genres = client.genres()
            let (requests, library) = try await (status, genres)
            self.status = requests
            self.genres = library
            // The rows are extras on top of the genres: Seerr failing leaves them out rather than the whole screen.
            rows = requests.configured ? ((try? await client.discoverRows()) ?? rows) : []
            if !requests.configured { mode = .browse }
            loaded = true
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}

/// Any film or show TMDB knows, to find something new and request it: the Apple TV's own search screen, keyboard
/// first, then shows and films.
struct DiscoverSearchView: View {
    @Environment(AppModel.self) private var model
    @State private var query = ""
    @State private var results: [ExternalTitle]?
    @State private var searched = ""
    @State private var error: String?

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 30) {
                if let results {
                    if results.isEmpty {
                        StatusView(state: .empty("Nothing found for \u{201C}\(searched)\u{201D}. Try a different spelling, or the original title."))
                            .frame(height: 300)
                    } else {
                        let owned = results.filter { $0.libraryItemId != nil }.count
                        Text(owned > 0 ? "\(owned) already in your library. Request anything else to add it." : "Request anything to add it to your library.")
                            .font(.system(size: 26, weight: .medium))
                            .foregroundStyle(Color.usher)
                            .padding(.horizontal, Layout.edge)
                        let shows = results.filter(\.isShow)
                        let films = results.filter { !$0.isShow }
                        if !shows.isEmpty { DiscoverRow(title: "Shows", titles: shows) }
                        if !films.isEmpty { DiscoverRow(title: "Films", titles: films) }
                    }
                } else if let error {
                    StatusView(state: .failed(error) { Task { await search() } })
                        .frame(height: 300)
                }
            }
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .ignoresSafeArea(edges: .horizontal)
        .background(RoomBackground(ambient: nil))
        .searchable(text: $query, prompt: "Any film or show")
        .task(id: query) {
            // Once typing pauses, so each letter doesn't go to Seerr.
            try? await Task.sleep(for: .milliseconds(350))
            guard !Task.isCancelled else { return }
            await search()
        }
    }

    private func search() async {
        let text = query.trimmingCharacters(in: .whitespaces)
        guard text.count >= 2, let client = model.client else {
            results = nil
            return
        }
        do {
            let found = try await client.searchEverything(text)
            guard text == query.trimmingCharacters(in: .whitespaces) else { return }
            results = found
            searched = text
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}

/// A row of films and shows from outside the library.
struct DiscoverRow: View {
    let title: String
    let titles: [ExternalTitle]
    /// Optional shared focus state, so the screen can steer focus to a card (by `focusPrefix/title id`).
    var focus: FocusState<String?>.Binding?
    var focusPrefix = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            RowHeading(title: title)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                    ForEach(titles) { ExternalCard(title: $0, focus: focus, focusID: "\(focusPrefix)/\($0.id)") }
                }
                .padding(.horizontal, Layout.edge)
                .padding(.top, 28)
                .padding(.bottom, 40)
            }
            .scrollClipDisabled()
            .focusSection()
        }
    }
}

/// A film or show from Discover: its poster, then the title and whether it's in the library or requested. One the
/// library has opens as itself.
struct ExternalCard: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let title: ExternalTitle
    var width: CGFloat = Layout.posterWidth
    var focus: FocusState<String?>.Binding?
    var focusID: String?

    var body: some View {
        ArtworkButton(ambient: .silver, focus: focus, focusID: focusID) {
            if let id = title.libraryItemId {
                router.push(.itemID(id))
            } else {
                router.push(.external(title))
            }
        } artwork: {
            RemoteImage(url: model.client?.imageURL(title.poster, width: 342)) {
                ZStack {
                    LinearGradient(colors: [Color.silver.opacity(0.3), Color.seat], startPoint: .top, endPoint: .bottom)
                    Text(title.title)
                        .font(.marquee(30, weight: .bold))
                        .foregroundStyle(Color.screen.opacity(0.8))
                        .multilineTextAlignment(.center)
                        .padding(16)
                }
            }
            .frame(width: width, height: width * 1.5)
            .overlay(alignment: .topLeading) {
                if title.isInLibrary { LibraryBadge().padding(10) }
            }
            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(title.title)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.8))
                    .lineLimit(1)
                Text([title.year.map(String.init), title.state].compactMap { $0 }.joined(separator: "  "))
                    .font(.system(size: 21))
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
            }
            .frame(width: width, alignment: .leading)
        }
    }
}

extension ExternalTitle {
    /// Ready to watch: the poster says so with a badge (see `LibraryBadge`).
    var isInLibrary: Bool { libraryItemId != nil || availability == .available }

    /// "Requested" or "Partly in your library", under the title; nil for something that's neither, or that's in.
    var state: String? {
        isInLibrary ? nil : availability.label
    }
}

/// On a poster from Discover: it's in the library, ready to play. Gold, top-left, away from the watched tick's corner;
/// a play mark rather than a tick, which on a poster means watched.
private struct LibraryBadge: View {
    var body: some View {
        Image(systemName: "play.fill")
            .font(.system(size: 16, weight: .heavy))
            .foregroundStyle(Color.room)
            .offset(x: 1)
            .frame(width: 36, height: 36)
            .background(Circle().fill(Color.inLibrary))
            .accessibilityLabel("In your library")
    }
}

extension Availability {
    var label: String? {
        switch self {
        case .none: nil
        case .requested: "Requested"
        case .partlyAvailable: "Partly in your library"
        case .available: "In your library"
        }
    }
}

/// A genre: a backdrop from its best-rated title, its name and how much of it there is beneath.
private struct GenreTile: View {
    @Environment(AppModel.self) private var model
    let genre: Genre
    let action: () -> Void

    /// Five across the content width: a genre is a way in, not something to look at, so smaller than a row's cards.
    static let across = 5
    static let spacing: CGFloat = 40
    static let width: CGFloat = (1920 - 2 * Layout.edge - CGFloat(across - 1) * spacing) / CGFloat(across)

    var body: some View {
        ArtworkButton(ambient: .silver, action: action) {
            RemoteImage(url: model.client?.imageURL(genre.backdrop, width: 640)) {
                LinearGradient(colors: [Color.silver.opacity(0.3), Color.seat], startPoint: .topLeading, endPoint: .bottomTrailing)
            }
            .frame(width: Self.width, height: Self.width * 9 / 16)
            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(genre.name)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.85))
                    .lineLimit(1)
                Text(counts)
                    .font(.system(size: 20))
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
            }
            .frame(width: Self.width, alignment: .leading)
        }
    }

    private var counts: String {
        [genre.movies > 0 ? (genre.movies == 1 ? "1 film" : "\(genre.movies) films") : nil,
         genre.shows > 0 ? (genre.shows == 1 ? "1 show" : "\(genre.shows) shows") : nil]
            .compactMap { $0 }.joined(separator: " and ")
    }
}

// MARK: - A title from outside the library

/// A film or show from Discover that isn't in the library: what it is, how it's rated, who's in it, and a request.
struct ExternalTitleView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let preview: ExternalTitle
    @State private var detail: ExternalDetail?
    @State private var error: String?
    @State private var scrollOffset: CGFloat = 0

    var body: some View {
        ZStack {
            Color.room.ignoresSafeArea()
            if let id = detail?.libraryItemId {
                // It's come in since: it opens as itself.
                DetailView(id: id, preview: nil)
            } else {
                backdrop
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 56) {
                        header
                            .frame(minHeight: 860, alignment: .bottomLeading)
                        if let detail {
                            sections(detail)
                        } else if let error {
                            StatusView(state: .failed(error) { Task { await load() } }).frame(height: 300)
                        }
                    }
                    .padding(.bottom, 90)
                }
                .scrollClipDisabled()
                .onScrollGeometryChange(for: CGFloat.self) { $0.contentOffset.y } action: { _, y in
                    scrollOffset = y
                }
            }
        }
        .ignoresSafeArea(edges: [.top, .horizontal])
        .task { await load() }
    }

    private var title: ExternalTitle { detail?.summary ?? preview }

    private var backdrop: some View {
        ZStack {
            RemoteImage(url: model.client?.imageURL(title.backdrop, width: 1920)) { Color.room }
                .frame(width: 1920, height: 1080)
                .clipped()
            LinearGradient(stops: [.init(color: .room.opacity(0.95), location: 0), .init(color: .room.opacity(0.6), location: 0.45), .init(color: .clear, location: 0.8)],
                           startPoint: .leading, endPoint: .trailing)
            LinearGradient(stops: [.init(color: .clear, location: 0.35), .init(color: .room.opacity(0.9), location: 0.9), .init(color: .room, location: 1)],
                           startPoint: .top, endPoint: .bottom)
            // Dim further as the page scrolls so rows stay legible over the artwork.
            Color.room.opacity(min(0.85, max(0, scrollOffset) / 700))
        }
        .ignoresSafeArea()
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 22) {
            Text(title.title)
                .font(.marquee(title.title.count > 24 ? 84 : 110, weight: .heavy))
                .foregroundStyle(Color.screen)
                .lineLimit(2)
                .minimumScaleFactor(0.6)
                .frame(maxWidth: 1100, alignment: .leading)
            if let tagline = detail?.tagline {
                Text(tagline)
                    .font(.system(size: 28, weight: .medium))
                    .italic()
                    .foregroundStyle(Color.screen.opacity(0.85))
            }
            HStack(spacing: 22) {
                FactsLine(facts: facts)
                if let genres = detail?.genres, !genres.isEmpty {
                    Text(genres.prefix(3).joined(separator: ", "))
                        .font(.system(size: 26, weight: .medium))
                        .foregroundStyle(Color.usher)
                }
            }
            if let detail, !ratings(detail).isEmpty {
                HStack(spacing: 44) {
                    ForEach(ratings(detail), id: \.label) { rating in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(rating.value)
                                .font(.system(size: 34, weight: .bold))
                                .foregroundStyle(Color.screen)
                                .monospacedDigit()
                            Text(rating.label)
                                .font(.system(size: 20, weight: .semibold))
                                .foregroundStyle(Color.usher)
                        }
                    }
                }
                .accessibilityElement(children: .combine)
            }
            if let overview = detail?.overview ?? preview.overview, !overview.isEmpty {
                Text(overview)
                    .font(.system(size: 27))
                    .lineSpacing(5)
                    .foregroundStyle(Color.screen.opacity(0.88))
                    .lineLimit(4)
                    .frame(maxWidth: 1040, alignment: .leading)
            }
            RequestButton(title: title)
                .padding(.top, 14)
        }
        .padding(.leading, Layout.edge)
        .padding(.trailing, 300)
        .padding(.bottom, 10)
    }

    /// "R   2026   1h 59m   Film".
    private var facts: [String] {
        guard let detail else { return [title.year.map(String.init), title.isShow ? "Series" : "Film"].compactMap { $0 } }
        let length: String? = detail.isShow
            ? detail.seasons.map { $0 == 1 ? "1 season" : "\($0) seasons" }
            : detail.runtimeMinutes.flatMap { Format.runtime(minutes: $0) }
        let status = detail.status.flatMap { ["Released", "Ended"].contains($0) ? nil : $0 }
        return [detail.certification, detail.year.map(String.init), length, detail.isShow ? "Series" : "Film", status].compactMap { $0 }
    }

    /// The scores people check before watching: TMDB, IMDb, Rotten Tomatoes critics and audience.
    private func ratings(_ detail: ExternalDetail) -> [(label: String, value: String)] {
        var scores: [(label: String, value: String)] = []
        if let tmdb = detail.tmdbRating { scores.append(("TMDB", String(format: "%.1f", tmdb))) }
        if let imdb = detail.imdbRating { scores.append(("IMDb", String(format: "%.1f", imdb))) }
        if let critics = detail.criticScore { scores.append(("Critics", "\(critics)%")) }
        if let audience = detail.audienceScore { scores.append(("Audience", "\(audience)%")) }
        return scores
    }

    @ViewBuilder
    private func sections(_ detail: ExternalDetail) -> some View {
        if !detail.cast.isEmpty {
            VStack(alignment: .leading, spacing: 0) {
                RowHeading(title: "Cast")
                // Not buttons: these people aren't in the library, so there's nowhere for them to go.
                ScrollView(.horizontal) {
                    HStack(alignment: .top, spacing: 36) {
                        ForEach(Array(detail.cast.enumerated()), id: \.offset) { _, person in
                            CastFace(person: person)
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                    .padding(.vertical, 30)
                }
                .scrollClipDisabled()
            }
        }
        if !detail.madeBy.isEmpty || detail.studio != nil {
            HStack(alignment: .top, spacing: 80) {
                if !detail.madeBy.isEmpty {
                    labeled(detail.isShow ? "Created by" : "Directed by", detail.madeBy.joined(separator: ", "))
                }
                if let studio = detail.studio {
                    labeled(detail.isShow ? "Network" : "Studio", studio)
                }
            }
            .padding(.horizontal, Layout.edge)
        }
        if !detail.recommendations.isEmpty {
            DiscoverRow(title: "If you like this", titles: detail.recommendations)
        }
    }

    private func labeled(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(label).font(.system(size: 22, weight: .semibold)).foregroundStyle(Color.usher)
            Text(value).font(.system(size: 26, weight: .medium)).foregroundStyle(Color.screen)
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            detail = try await client.externalTitle(preview)
            error = nil
        } catch FlexarrClientError.http(status: 404, _) {
            error = "Flexarr can’t find that title."
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}

/// Someone in the cast: their face (or initials), name and part.
private struct CastFace: View {
    @Environment(AppModel.self) private var model
    let person: ExternalPerson

    var body: some View {
        VStack(spacing: 16) {
            RemoteImage(url: model.client?.imageURL(person.image, width: 300)) {
                ZStack {
                    Color.seat
                    Text(person.name.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined())
                        .font(.marquee(56, weight: .bold))
                        .foregroundStyle(Color.usher)
                }
            }
            .frame(width: 180, height: 180)
            .clipShape(Circle())
            VStack(spacing: 2) {
                Text(person.name).font(.system(size: 23, weight: .semibold)).foregroundStyle(Color.screen)
                if let character = person.character { Text(character).font(.system(size: 20)).foregroundStyle(Color.usher) }
            }
            .lineLimit(1)
            .frame(width: 210)
        }
    }
}

/// Request the title through Seerr, or say where it stands if it's already asked for or in.
private struct RequestButton: View {
    @Environment(AppModel.self) private var model
    let title: ExternalTitle

    private enum Sending: Equatable {
        case idle, sending, sent
        case failed(String)
    }

    @State private var sending = Sending.idle

    var body: some View {
        HStack(spacing: 30) {
            // One button throughout, whatever it says: one that's taken away or disabled while it has focus leaves
            // nothing on the page focused, and then Menu leaves the app rather than going back.
            Button {
                guard canRequest, sending != .sending else { return }
                Task { await request() }
            } label: {
                Label(label, systemImage: symbol)
            }
            .buttonStyle(ActionButtonStyle(prominent: canRequest))
            if let failure {
                Text(failure)
                    .font(.system(size: 24, weight: .medium))
                    .foregroundStyle(Color.screen.opacity(0.85))
                    .lineLimit(2)
                    .frame(maxWidth: 700, alignment: .leading)
            }
        }
        .focusSection()
    }

    /// Not asked for yet, here or anywhere else.
    private var canRequest: Bool { sending != .sent && title.availability == .none }

    private var label: String {
        if sending == .sent { return "Requested" }
        if let state = title.availability.label { return state }
        if sending == .sending { return "Requesting…" }
        return failure == nil ? "Request" : "Try again"
    }

    private var symbol: String {
        if sending == .sent { return "clock" }
        switch title.availability {
        case .available: return "checkmark"
        case .requested, .partlyAvailable: return "clock"
        case .none: return "plus"
        }
    }

    private var failure: String? {
        if case .failed(let message) = sending { return message }
        return nil
    }

    private func request() async {
        guard let client = model.client else { return }
        sending = .sending
        do {
            try await client.request(title)
            sending = .sent
        } catch {
            sending = .failed(ConnectionError(error).message)
        }
    }
}

// MARK: - A genre

/// Everything the library has in a genre, best rated first, narrowed to films or shows if wanted.
struct GenreView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let name: String
    @State private var kind: MediaKind?
    @State private var page: Paged<ItemSummary>?
    @State private var error: String?

    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 40) {
                HStack(alignment: .lastTextBaseline, spacing: 40) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(name).font(.marquee(72, weight: .heavy)).foregroundStyle(Color.screen)
                        Text(page.map { "\($0.total.formatted()) \($0.total == 1 ? "title" : "titles"), best rated first" } ?? " ")
                            .font(.system(size: 26)).foregroundStyle(Color.usher)
                    }
                    Spacer()
                    HStack(spacing: 16) {
                        ForEach([(nil, "Everything"), (MediaKind.movie, "Films"), (MediaKind.series, "Shows")], id: \.1) { choice, label in
                            Button(label) { kind = choice }
                                .buttonStyle(PillButtonStyle(selected: kind == choice))
                        }
                    }
                    .focusSection()
                }
                if let page {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 64) {
                        ForEach(page.items) { item in
                            PosterCard(item: item) { router.push(.item(item)) }
                        }
                    }
                    .focusSection()
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 400)
                } else {
                    ProgressView().frame(maxWidth: .infinity).padding(60)
                }
            }
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .background(RoomBackground(ambient: nil))
        .task(id: kind) { await load() }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            page = try await client.genreItems(name, kind: kind)
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}
