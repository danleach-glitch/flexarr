import FlexarrKit
import SwiftUI

struct HomeView: View {
    @Environment(AppModel.self) private var model
    @State private var home: Home?
    @State private var error: String?
    @State private var featuredIndex = 0
    /// `model.refreshToken` when `home` was last fetched.
    @State private var loadedToken: Int?
    @State private var loadedAt: Date?
    @State private var isVisible = false
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        ZStack {
            RoomBackground(ambient: featured?.glowColor, intensity: 0.28)
                .animation(.easeInOut(duration: 0.8), value: featured?.id)
            if let home {
                content(home)
            } else if let error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .task(id: model.refreshToken) {
            // Coming back from a detail screen or another tab runs this again; fetch only when something changed
            // here, or what's shown is old enough that something may have changed elsewhere.
            guard loadedToken != model.refreshToken || Freshness.isStale(loadedAt) else { return }
            await load()
        }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            // Back to the app after a while (tvOS keeps it running for days).
            if phase == .active, isVisible, Freshness.isStale(loadedAt) { Task { await load() } }
        }
    }

    private var featured: ItemSummary? {
        guard let items = home?.featured, !items.isEmpty else { return nil }
        return items[featuredIndex % items.count]
    }

    private func content(_ home: Home) -> some View {
        ScrollView(.vertical) {
            LazyVStack(alignment: .leading, spacing: 30) {
                if !home.featured.isEmpty {
                    HomeHero(items: home.featured, overviews: home.overviews, index: $featuredIndex, resuming: home.resuming)
                } else if let message = EmptyHomeMessage(libraries: model.libraries, limitedProfile: model.hasAgeLimit ? model.currentProfile?.name : nil) {
                    // Above whatever else Home has (Coming soon, channels).
                    EmptyHome(message: message)
                }
                // A row with nothing in it (a kind this app doesn't show yet, say) would be a bare heading. That includes
                // the empty row placing New podcasts (`HomeRow.podcastsRowId`): the Apple TV doesn't play podcasts.
                // Coming soon's titles come separately, in `Home.upcoming`.
                ForEach(home.rows.filter { $0.style == .upcoming ? !(home.upcoming ?? []).isEmpty : $0.hasContent }) { row in
                    if row.style == .upcoming {
                        ComingSoonRow(items: home.upcoming ?? [], downloading: home.downloading ?? 0)
                    } else {
                        HomeRowView(row: row)
                    }
                }
            }
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        // Under the hero; with none (only Live TV so far), the rows start below the sidebar's label at the top.
        .ignoresSafeArea(edges: home.featured.isEmpty ? [.horizontal] : [.top, .horizontal])
    }

    private func load() async {
        guard let client = model.client else { return }
        let token = model.refreshToken
        do {
            // Books in progress join Continue watching (for profiles that want them there) and play in the audio player.
            // Not podcasts: the Apple TV doesn't play them.
            let loaded = try await client.home(listening: [.books])
            // Nothing yet: the libraries say whether a scan is under way or their folders hold nothing.
            if loaded.featured.isEmpty { await model.loadLibraries() }
            home = loaded
            loadedToken = token
            loadedAt = Date()
            error = nil
        } catch {
            if home == nil { self.error = ConnectionError(error).message }
        }
    }
}

/// Home with nothing to show: no library yet, a first scan under way, or libraries with nothing recognised in them.
private struct EmptyHome: View {
    let message: EmptyHomeMessage

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            Image(systemName: "film.stack")
                .font(.system(size: 76, weight: .semibold))
                .foregroundStyle(Color.silver)
            Text(message.title)
                .font(.marquee(64, weight: .black))
                .foregroundStyle(Color.screen)
            if !message.body.isEmpty {
                Text(message.body)
                    .font(.title3)
                    .foregroundStyle(Color.usher)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if !message.folders.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(message.folders, id: \.self) { folder in
                        Text(folder)
                            .font(.callout.monospaced())
                            .foregroundStyle(Color.screen)
                    }
                }
            }
        }
        .frame(maxWidth: 1100, alignment: .leading)
        .padding(.top, 160)
        .padding(.horizontal, Layout.edge)
        // Somewhere for focus to be: with nothing focusable on the screen, the remote couldn't reach the sidebar again
        // once it had folded away.
        .focusable()
    }
}

/// Full-bleed featured hero: backdrop, clear logo, facts, one-line overview, Play and More info.
/// Left on Play or right on More info moves between featured titles; it also advances on its own.
struct HomeHero: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let items: [ItemSummary]
    let overviews: [String: String?]
    @Binding var index: Int
    /// What a title's Play picks up from, when it's been started.
    var resuming: (ItemSummary) -> ItemSummary? = { _ in nil }
    /// Controls at the top-left, under the sidebar's label (a library's Browse and All switch); up from Play reaches them.
    var top: AnyView?
    @Environment(\.focusesContent) private var takesFocus
    @FocusState private var focusedButton: HeroButton?
    @Namespace private var heroScope

    private enum HeroButton { case play, info }

    private var item: ItemSummary { items[index % items.count] }

    var body: some View {
        ZStack(alignment: .bottomLeading) {
            backdrop
                .id(item.id)
                .transition(.opacity)

            VStack(alignment: .leading, spacing: 26) {
                VStack(alignment: .leading, spacing: 24) {
                    TitleArt(item: item, maxWidth: 760, maxHeight: 230, fontSize: 110)
                    FactsLine(facts: item.facts(scores: !model.isKids))
                    if let overview = overviews[item.id] ?? nil {
                        Text(overview)
                            .font(.system(size: 27))
                            .foregroundStyle(Color.screen.opacity(0.85))
                            .lineLimit(2)
                            .lineSpacing(4)
                            .frame(maxWidth: 900, alignment: .leading)
                    }
                }
                .id(item.id)
                .transition(.opacity)

                HStack(spacing: 28) {
                    Button { play() } label: {
                        Label(resuming(item) != nil || item.isResumable ? "Resume" : "Play", systemImage: "play.fill")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: item.glowColor, prominent: true))
                    .focused($focusedButton, equals: .play)
                    .prefersDefaultFocus(true, in: heroScope)
                    .onMoveCommand { if $0 == .left { step(-1) } }

                    Button { router.push(.item(item)) } label: {
                        Label("More info", systemImage: "info.circle")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: item.glowColor))
                    .focused($focusedButton, equals: .info)
                    .onMoveCommand { if $0 == .right { step(1) } }
                }
                .padding(.top, 10)

                if items.count > 1 {
                    HStack(spacing: 12) {
                        ForEach(items.indices, id: \.self) { i in
                            Capsule()
                                .fill(i == index % items.count ? Color.screen : Color.screen.opacity(0.3))
                                .frame(width: i == index % items.count ? 34 : 12, height: 12)
                        }
                    }
                    .padding(.top, 6)
                }
            }
            .padding(.leading, Layout.edge)
            .padding(.bottom, 40)
            // The focusable part of the hero is this corner. Coming in from the sidebar (or down from the switch above)
            // the focus engine looks for a section beside it; one spanning the whole hero isn't, and the rows' second
            // poster was winning.
            .focusSection()

            if let top {
                top
                    .focusSection()
                    .padding(.leading, Layout.edge)
                    .padding(.top, LibraryViewSwitch.topInset)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }
        }
        .frame(height: 900)
        .frame(maxWidth: .infinity)
        // Coming in from the sidebar (or back up from the rows) lands on Play.
        .focusScope(heroScope)
        .defaultFocus($focusedButton, .play, priority: .userInitiated)
        .onChange(of: focusedButton) { old, new in
            // The focus engine picks the geometrically nearest button (often More info, nearer
            // the sidebar); entering the hero should always land on Play.
            if old == nil, new == .info { focusedButton = .play }
        }
        .onAppear(perform: takeFocus)
        .onChange(of: takesFocus.wrappedValue, takeFocus)
        .task(id: HeroClock(index: index, focused: focusedButton != nil)) {
            // While the hero has focus, crossfade to the next title after a while; a manual
            // step restarts the clock. Browsing the rows below leaves the room colour alone.
            guard focusedButton != nil else { return }
            try? await Task.sleep(for: .seconds(12))
            guard !Task.isCancelled else { return }
            step(1)
        }
    }

    /// From the library's switch as the tab opens, or from the sidebar as the app opens (see `focusesContent`).
    private func takeFocus() {
        guard takesFocus.wrappedValue else { return }
        takesFocus.wrappedValue = false
        // As the app opens, the focus engine is still settling on the sidebar and can take one request back: keep
        // asking for a moment.
        Task {
            for _ in 0..<8 where focusedButton == nil {
                focusedButton = .play
                try? await Task.sleep(for: .milliseconds(150))
            }
        }
    }

    private var backdrop: some View {
        RemoteImage(url: model.client?.imageURL(item.artwork.backdrop, width: 1920)) {
            Color.clear
        }
        .frame(width: 1920, height: 1080)
        .clipped()
        .overlay {
            // A scrim so text stays legible, then the ambient colour bleeding in from the corner.
            LinearGradient(stops: [.init(color: .room.opacity(0.92), location: 0), .init(color: .room.opacity(0.55), location: 0.4), .init(color: .clear, location: 0.75)],
                           startPoint: .leading, endPoint: .trailing)
            LinearGradient(colors: [item.glowColor.opacity(0.3), .clear], startPoint: .bottomLeading, endPoint: .topTrailing)
                .blendMode(.plusLighter)
                .opacity(0.6)
        }
        // Fade out rather than painting the room colour, so there is no seam with the rows below.
        .mask(LinearGradient(stops: [.init(color: .black, location: 0), .init(color: .black, location: 0.55), .init(color: .clear, location: 0.95)],
                             startPoint: .top, endPoint: .bottom))
        .frame(height: 900, alignment: .top)
    }

    private func step(_ delta: Int) {
        withAnimation(.easeInOut(duration: 0.6)) {
            index = (index + delta + items.count) % items.count
        }
    }

    private func play() {
        let item = item
        if let resume = resuming(item) {
            model.play(resume)
        } else {
            Task { await model.playBest(item, router: router) }
        }
    }
}

private struct HeroClock: Hashable {
    let index: Int
    let focused: Bool
}

/// Clear logo when the title has one, otherwise the title set in marquee type.
struct TitleArt: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    var maxWidth: CGFloat
    var maxHeight: CGFloat
    var fontSize: CGFloat

    var body: some View {
        if let logo = model.client?.imageURL(item.artwork.logo, width: 800) {
            RemoteImage(url: logo, contentMode: .fit) {
                Color.clear
            }
            .frame(maxWidth: maxWidth, maxHeight: maxHeight, alignment: .bottomLeading)
            .modifier(TitleScrim(reach: maxWidth * 0.6))
            .accessibilityLabel(item.title)
        } else {
            title
        }
    }

    private var title: some View {
        Text(item.displayTitle)
            .font(.marquee(fontSize, weight: .black))
            .kerning(-1)
            .foregroundStyle(Color.screen)
            .lineLimit(2)
            .minimumScaleFactor(0.6)
            .frame(maxWidth: maxWidth * 1.3, alignment: .leading)
            .modifier(TitleScrim(reach: maxWidth * 0.6))
    }
}

struct HomeRowView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let row: HomeRow
    @FocusState private var focusedCard: String?
    /// The card focused here last, kept while it leaves the row (finished, say) so focus can go to the one that takes
    /// its place rather than back to the row's start. Cleared when focus moves elsewhere.
    @State private var lastFocused: String?
    /// The card whose long-press menu was just used to mark or remove it (see `leavesRow`). Noted then, because by
    /// the time the row reloads tvOS may already have moved focus on to a neighbour.
    @State private var leaving: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            RowHeading(title: row.title)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: row.style == .top10 ? 30 : Layout.rowSpacing) {
                    // A playlists row carries playlists rather than items.
                    ForEach(row.playlists ?? []) { playlist in
                        PlaylistCard(playlist: playlist) { router.push(.playlist(playlist)) }
                    }
                    // A mixes row carries the day's mixes; pressing one plays it.
                    ForEach(row.mixes ?? []) { mix in
                        MixCard(mix: mix)
                    }
                    // A Live TV row carries the profile's favourite channels; channel up and down go through them.
                    ForEach(row.channels ?? []) { channel in
                        HomeChannelCard(channel: channel, order: row.channels ?? [])
                    }
                    ForEach(Array(row.items.enumerated()), id: \.element.id) { index, item in
                        switch row.style {
                        case .wide:
                            WideCard(item: item, focus: $focusedCard) { model.play(item) }
                                .environment(\.inContinueWatching, row.id == "continue")
                        case .top10:
                            Top10Card(rank: index + 1, item: item) { router.push(.item(item)) }
                        case .tracks:
                            // A row of tracks plays: the one pressed first, the rest of the row after it.
                            TrackCard(item: item) { model.playTracks(row.items, from: index) }
                        case _ where item.kind == .audiobook:
                            // Books (Continue listening, Next in series, New audiobooks): square covers that open the book.
                            BookCard(item: item, focus: $focusedCard) { router.push(.item(item)) }
                        default:
                            PosterCard(item: item, focus: $focusedCard) { router.push(.item(item)) }
                        }
                    }
                    if let library = linkedLibrary {
                        SeeAllCard(style: row.style, square: isBooks) { router.push(.library(library.library, library.sort, genre: library.genre)) }
                    } else if row.link == "/watchlist" {
                        SeeAllCard(style: row.style, square: isBooks) { router.push(.watchlist) }
                    }
                }
                .padding(.horizontal, Layout.edge)
                .padding(.top, 28)
                .padding(.bottom, 40)
            }
            .scrollClipDisabled()
            .focusSection()
            .environment(\.leavesRow) { leaving = $0 }
        }
        .onChange(of: focusedCard) { _, id in
            if let id {
                lastFocused = id
            } else if let last = lastFocused, row.items.contains(where: { $0.id == last }) {
                lastFocused = nil // moved out of the row, rather than the card going
            }
        }
        .onChange(of: row.items.map(\.id)) { old, new in
            let gone = [leaving, lastFocused].compactMap(\.self).first { old.contains($0) && !new.contains($0) }
            leaving = nil
            guard let gone, let index = old.firstIndex(of: gone), !new.isEmpty else { return }
            // The card that slid into its place, or the new last one.
            let next = new[min(index, new.count - 1)]
            lastFocused = next
            Task {
                focusedCard = next
                // tvOS moves focus to a neighbour of the card that went, sometimes after this; set it again once settled.
                try? await Task.sleep(for: .milliseconds(200))
                if focusedCard != next { focusedCard = next }
            }
        }
    }

    /// A row of books, whose square covers the See all card matches.
    private var isBooks: Bool {
        row.style != .wide && row.style != .top10 && !row.items.isEmpty && row.items.allSatisfy { $0.kind == .audiobook }
    }

    /// Rows link to "/library/{id}?view=all&sort=added" (or "&genre=Drama"); map that to a library destination.
    private var linkedLibrary: (library: Library, sort: LibrarySort, genre: String?)? {
        guard let link = row.link, let components = URLComponents(string: link),
              components.path.hasPrefix("/library/") else { return nil }
        let id = String(components.path.dropFirst("/library/".count))
        guard let library = model.libraries.first(where: { $0.id == id }) else { return nil }
        let query = components.queryItems ?? []
        let sort = query.first(where: { $0.name == "sort" })?.value.flatMap(LibrarySort.init(rawValue:)) ?? .title
        return (library, sort, query.first(where: { $0.name == "genre" })?.value)
    }
}

struct SeeAllCard: View {
    let style: HomeRowStyle
    /// Beside square covers (books), square too.
    var square = false
    let action: () -> Void

    var body: some View {
        let size = style == .wide ? CGSize(width: Layout.wideWidth, height: Layout.wideWidth * 9 / 16)
            : CGSize(width: Layout.posterWidth, height: Layout.posterWidth * (square ? 1 : 1.5))
        Button(action: action) {
            VStack(spacing: 16) {
                Image(systemName: "square.grid.2x2").font(.system(size: 48, weight: .semibold))
                Text("See all").font(.system(size: 28, weight: .semibold))
            }
            .foregroundStyle(Color.screen.opacity(0.85))
            .frame(width: size.width, height: size.height)
            .background(Color.seat)
        }
        .buttonStyle(.card)
    }
}

/// What Sonarr, Radarr and Readarr expect soon: episodes airing, films and books coming out, with any download under
/// way. Pressing one already in the library (a show's next episode) opens it.
private struct ComingSoonRow: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let items: [UpcomingItem]
    let downloading: Int

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            RowHeading(title: downloading > 0 ? "Coming soon (\(downloading) downloading)" : "Coming soon")
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                    ForEach(items) { item in
                        // Every card takes focus, so the row can be looked along; only those in the library open.
                        ArtworkButton(ambient: .silver, accessibilityText: "\(UpcomingDay.title(item)), \(UpcomingDay.label(item.date))") {
                            if let id = item.libraryItemId { router.push(.itemID(id)) }
                        } artwork: {
                            RemoteImage(url: model.client?.imageURL(item.still ?? item.poster, width: 780)) {
                                Text(UpcomingDay.title(item))
                                    .font(.system(size: 25, weight: .semibold))
                                    .foregroundStyle(Color.usher)
                                    .multilineTextAlignment(.center)
                                    .padding(20)
                            }
                            .frame(width: Layout.wideWidth, height: Layout.wideWidth * 9 / 16)
                            .background(Color.seat)
                            .overlay(alignment: .topLeading) {
                                Text(UpcomingDay.label(item.date))
                                    .font(.system(size: 21, weight: .semibold))
                                    .foregroundStyle(Color.screen)
                                    .padding(.horizontal, 12)
                                    .padding(.vertical, 6)
                                    .background(Capsule().fill(.black.opacity(0.6)))
                                    .padding(14)
                            }
                            .overlay(alignment: .bottom) {
                                if let progress = item.download?.progress {
                                    ProgressBar(value: progress).padding(16)
                                }
                            }
                            .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
                        } caption: { focused in
                            VStack(alignment: .leading, spacing: 4) {
                                Text(UpcomingDay.title(item))
                                    .font(.system(size: 25, weight: .semibold))
                                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.85))
                                Text(UpcomingDay.detail(item))
                                    .font(.system(size: 21))
                                    .foregroundStyle(Color.usher)
                            }
                            .lineLimit(1)
                            .frame(width: Layout.wideWidth, alignment: .leading)
                        }
                    }
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
