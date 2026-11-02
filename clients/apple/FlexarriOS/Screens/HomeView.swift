import FlexarrKit
import SwiftUI

struct HomeView: View {
    @Environment(AppModel.self) private var model
    @State private var home: Home?
    /// The newest unplayed episodes of the podcasts followed, for the New podcasts row: where Home's rows place it, or
    /// (servers before 0.58) after Continue watching.
    @State private var newEpisodes: [PodcastEpisode] = []
    /// The last Home placed the rows itself and had no New podcasts among them (the profile switched it off): the next
    /// load skips fetching episodes alongside it, and fetches them after only if that Home has the row again.
    @State private var podcastsRowOff = false
    @State private var error: String?
    @State private var featuredIndex = 0
    @State private var choosingProfile = false
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
        .toolbarBackground(.hidden, for: .navigationBar)
        .sheet(isPresented: $choosingProfile) {
            ProfilePickerView(marksCurrent: true)
                .presentationDetents([.medium, .large])
                .presentationBackground(Color.room)
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
            if phase == .active, isVisible, Freshness.isStale(loadedAt) { Task { await load() } }
        }
    }

    private var featured: ItemSummary? {
        guard let items = home?.featured, !items.isEmpty else { return nil }
        return items[featuredIndex % items.count]
    }

    private func content(_ home: Home) -> some View {
        // Read before the scroll view runs up under the status bar, to keep the profile picture below it.
        GeometryReader { proxy in
            ScrollView(.vertical) {
                // The whole screen when there's nothing else. With something to play (podcasts, Coming soon, channels) it
                // stays out of the way: "nothing to watch" would be wrong.
                if home.featured.isEmpty, !showsNewEpisodes(home), !home.rows.contains(where: \.hasContent), (home.upcoming ?? []).isEmpty,
                   let message = EmptyHomeMessage(libraries: model.libraries, limitedProfile: model.hasAgeLimit ? model.currentProfile?.name : nil) {
                    EmptyHome(message: message)
                        .containerRelativeFrame(.vertical)
                }
                LazyVStack(alignment: .leading, spacing: 28) {
                    if !home.featured.isEmpty {
                        HomeHero(items: home.featured, overviews: home.overviews, index: $featuredIndex, resuming: home.resuming)
                    }
                    if home.placesPodcasts == true {
                        // The server places New podcasts: an empty row where it goes (none when the profile switched it off).
                        ForEach(home.rows.filter { shows($0, in: home) || $0.id == HomeRow.podcastsRowId }) { row in
                            if row.id == HomeRow.podcastsRowId {
                                if !newEpisodes.isEmpty { NewEpisodesRow(episodes: newEpisodes) }
                            } else {
                                rowView(row, in: home)
                            }
                        }
                    } else {
                        // A row with nothing in it (a kind this app doesn't show yet, say) would be a bare heading.
                        // Servers before 0.58 leave New podcasts to the app: after Continue watching.
                        let rows = home.rows.filter { shows($0, in: home) }
                        let continuing = rows.first?.id == "continue" ? 1 : 0
                        ForEach(rows.prefix(continuing)) { rowView($0, in: home) }
                        if !newEpisodes.isEmpty {
                            NewEpisodesRow(episodes: newEpisodes)
                        }
                        ForEach(rows.dropFirst(continuing)) { rowView($0, in: home) }
                    }
                }
                .padding(.bottom, 32)
                // Part of the page, so it scrolls away with the hero rather than floating over the rows.
                .overlay(alignment: .topTrailing) {
                    profileButton.padding(.top, home.featured.isEmpty ? 4 : proxy.safeAreaInsets.top + 4).padding(.trailing, 16)
                }
            }
            // The hero runs up under the status bar; without one (a server of only podcasts, say) the rows start below it.
            .ignoresSafeArea(edges: home.featured.isEmpty ? [] : .top)
            .withoutTopEdgeEffect()
            .refreshable { await load() }
        }
    }

    private func shows(_ row: HomeRow, in home: Home) -> Bool { HomeRowSlot.shows(row, in: home) }

    private func rowView(_ row: HomeRow, in home: Home) -> some View { HomeRowSlot(row: row, home: home) }

    /// Whether the New podcasts row shows: there are new episodes, and the profile has it on Home.
    private func showsNewEpisodes(_ home: Home) -> Bool {
        guard !newEpisodes.isEmpty else { return false }
        return home.placesPodcasts != true || home.rows.contains { $0.id == HomeRow.podcastsRowId }
    }

    @ViewBuilder
    private var profileButton: some View {
        if model.offersProfilePicker, let current = model.currentProfile {
            Button { choosingProfile = true } label: {
                ProfileAvatar(profile: current, size: 44)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("Profile: \(current.name)")
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        let token = model.refreshToken
        do {
            // Empty without podcasts (none followed, no library, a server from before them: an error, also nothing).
            // None on a kids profile, which has no podcasts.
            let kids = model.isKids
            let skipped = kids || podcastsRowOff
            async let episodes = skipped ? [] : client.newPodcastEpisodes(unplayed: true, take: 20)
            // Books and podcast episodes in progress join Continue watching (for profiles that want them there); the
            // card plays them in the audio player.
            let loaded = try await client.home(listening: [.books, .podcasts])
            // Nothing yet: the libraries say whether a scan is under way or their folders hold nothing.
            if loaded.featured.isEmpty { await model.loadLibraries() }
            let hasRow = loaded.placesPodcasts != true || loaded.rows.contains { $0.id == HomeRow.podcastsRowId }
            var fetched = (try? await episodes) ?? []
            if skipped, !kids, hasRow {
                fetched = (try? await client.newPodcastEpisodes(unplayed: true, take: 20)) ?? []
            }
            podcastsRowOff = !hasRow
            home = loaded
            newEpisodes = hasRow ? fetched : []
            loadedToken = token
            loadedAt = Date()
            error = nil
        } catch {
            if home == nil { self.error = ConnectionError(error).message }
        }
    }
}

/// The newest unplayed episodes of the podcasts followed: square artwork, the episode and its show. Tapping opens the
/// episode (its notes, Play, its download), as everywhere else episodes are listed.
struct NewEpisodesRow: View {
    @Environment(\.horizontalSizeClass) private var sizeClass
    let episodes: [PodcastEpisode]
    @State private var opened: PodcastEpisode?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: "New podcasts")
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                    ForEach(episodes) { episode in
                        Button { opened = episode } label: {
                            VStack(alignment: .leading, spacing: 4) {
                                PodcastArtwork(path: episode.artwork.thumb ?? episode.artwork.poster, title: episode.podcastTitle, accent: episode.accentColor, imageWidth: 342)
                                    .overlay(alignment: .bottom) {
                                        if let progress = episode.progress { ProgressBar(value: progress).padding(6) }
                                    }
                                Text(episode.title)
                                    .font(.subheadline.weight(.semibold))
                                    .foregroundStyle(Color.screen)
                                    .lineLimit(2)
                                    .multilineTextAlignment(.leading)
                                Text(episode.podcastTitle)
                                    .font(.caption)
                                    .foregroundStyle(Color.usher)
                                    .lineLimit(1)
                            }
                            .frame(width: Layout.posterWidth(sizeClass) * 1.15)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(CardButtonStyle())
                        .accessibilityLabel("\(episode.title), \(episode.podcastTitle)")
                        .accessibilityHint("Shows the episode")
                    }
                }
                .padding(.horizontal, Layout.edge)
            }
            .scrollIndicators(.hidden)
        }
        .sheet(item: $opened) { episode in
            EpisodeSheet(episode: episode)
                .presentationDetents([.medium, .large])
                .presentationBackground(Color.room)
        }
    }
}

/// Featured titles to swipe through: backdrop, clear logo, facts, a line of overview, Play and More info.
struct HomeHero: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.verticalSizeClass) private var verticalSizeClass
    let items: [ItemSummary]
    let overviews: [String: String?]
    @Binding var index: Int
    /// What a title's Play picks up from, when it's been started.
    var resuming: (ItemSummary) -> ItemSummary? = { _ in nil }

    /// Shorter on a phone held sideways, so the title and Play stay on screen.
    private var height: CGFloat { verticalSizeClass == .compact ? 340 : sizeClass == .regular ? 620 : 560 }

    var body: some View {
        TabView(selection: $index) {
            ForEach(Array(items.enumerated()), id: \.element.id) { offset, item in
                slide(item).tag(offset)
            }
        }
        .tabViewStyle(.page(indexDisplayMode: items.count > 1 ? .always : .never))
        .frame(height: height)
        .task(id: index) {
            // Moves on by itself after a while; a swipe restarts the clock.
            guard items.count > 1, (try? await Task.sleep(for: .seconds(10))) != nil else { return }
            withAnimation(.easeInOut(duration: 0.6)) { index = (index + 1) % items.count }
        }
    }

    private func slide(_ item: ItemSummary) -> some View {
        ZStack(alignment: .bottomLeading) {
            Color.clear
                .overlay {
                    RemoteImage(url: model.client?.imageURL(item.artwork.backdrop, width: sizeClass == .regular ? 1600 : 1000)) {
                        LinearGradient(colors: [item.glowColor.opacity(0.4), Color.room], startPoint: .top, endPoint: .bottom)
                    }
                }
                .clipped()
                .overlay {
                    LinearGradient(stops: [.init(color: .clear, location: 0.25), .init(color: .room.opacity(0.7), location: 1)],
                                   startPoint: .top, endPoint: .bottom)
                    LinearGradient(colors: [item.glowColor.opacity(0.3), .clear], startPoint: .bottomLeading, endPoint: .topTrailing)
                        .blendMode(.plusLighter)
                        .opacity(0.5)
                }
                // Fade out rather than painting the room colour, so there's no seam with the rows below.
                .mask(LinearGradient(stops: [.init(color: .black, location: 0.5), .init(color: .clear, location: 1)], startPoint: .top, endPoint: .bottom))

            VStack(alignment: .leading, spacing: 12) {
                TitleArt(item: item, maxWidth: 280, maxHeight: 110, fontSize: 52)
                FactsLine(facts: item.facts(scores: !model.isKids))
                if let overview = overviews[item.id] ?? nil {
                    Text(overview)
                        .font(.subheadline)
                        .foregroundStyle(Color.screen.opacity(0.85))
                        .lineLimit(2)
                        .frame(maxWidth: 520, alignment: .leading)
                }
                HStack(spacing: 10) {
                    let resume = resuming(item)
                    Button {
                        if let resume { model.play(resume) } else { Task { await model.playBest(item, router: router) } }
                    } label: {
                        Label(resume != nil || item.isResumable ? "Resume" : "Play", systemImage: "play.fill")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: item.ambientColor, prominent: true))
                    Button { router.push(.item(item)) } label: {
                        Label("More info", systemImage: "info.circle")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: item.glowColor))
                }
                .padding(.top, 4)
            }
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 44)
        }
        .contentShape(Rectangle())
        .onTapGesture { router.push(.item(item)) }
    }
}

/// One of Home's rows, or of a library's Browse: Coming soon from the titles that travel beside the rows, any other
/// from its own.
struct HomeRowSlot: View {
    let row: HomeRow
    let home: Home

    /// Whether a row has something to show. Coming soon's titles come separately, in `Home.upcoming`.
    static func shows(_ row: HomeRow, in home: Home) -> Bool {
        row.style == .upcoming ? !(home.upcoming ?? []).isEmpty : row.hasContent
    }

    var body: some View {
        if row.style == .upcoming {
            ComingSoonRow(items: home.upcoming ?? [], downloading: home.downloading ?? 0)
        } else {
            HomeRowView(row: row)
        }
    }
}

struct HomeRowView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    let row: HomeRow

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: row.title, action: row.style == .live ? { router.push(.liveTv) }
                : row.link == "/watchlist" ? { router.push(.watchlist) }
                : linkedLibrary.map { link in { router.push(.library(link.library, link.sort, genre: link.genre)) } })
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: row.style == .top10 ? 6 : Layout.rowSpacing) {
                    // A playlists row carries playlists rather than items.
                    ForEach(row.playlists ?? []) { playlist in
                        PlaylistCard(playlist: playlist, width: Layout.posterWidth(sizeClass))
                    }
                    // A mixes row carries the day's mixes; tapping one plays it.
                    ForEach(row.mixes ?? []) { mix in
                        MixCard(mix: mix, width: Layout.posterWidth(sizeClass))
                    }
                    // A Live TV row carries the profile's favourite channels; channel up and down go through them.
                    ForEach(row.channels ?? []) { channel in
                        HomeChannelCard(channel: channel, order: row.channels ?? [], width: Layout.wideWidth(sizeClass) * 0.8)
                    }
                    ForEach(Array(row.items.enumerated()), id: \.element.id) { index, item in
                        switch row.style {
                        case .wide:
                            WideCard(item: item, width: Layout.wideWidth(sizeClass))
                                .environment(\.inContinueWatching, row.id == "continue")
                        case .top10:
                            Top10Card(rank: index + 1, item: item, width: Layout.posterWidth(sizeClass))
                        case .tracks:
                            // A row of tracks plays: the one tapped first, the rest of the row after it.
                            TrackCard(item: item, width: Layout.posterWidth(sizeClass)) { model.playTracks(row.items, from: index) }
                        default:
                            PosterCard(item: item, width: Layout.posterWidth(sizeClass))
                        }
                    }
                }
                .padding(.horizontal, Layout.edge)
            }
            .scrollIndicators(.hidden)
        }
    }

    /// Rows link to "/library/{id}?sort=added" (a library's Browse rows to "?view=all&sort=added" or "&genre=Drama");
    /// map that to a library destination. One with a sort or genre opens the library's titles rather than its Browse.
    private var linkedLibrary: (library: Library, sort: LibrarySort, genre: String?)? {
        guard let link = row.link, let components = URLComponents(string: link),
              components.path.hasPrefix("/library/") else { return nil }
        let id = String(components.path.dropFirst("/library/".count))
        guard let library = model.libraries.first(where: { $0.id == id }) else { return nil }
        let query = components.queryItems ?? []
        let sort = query.first(where: { $0.name == "sort" })?.value.flatMap(LibrarySort.init(rawValue:)) ?? .title
        let genre = query.first(where: { $0.name == "genre" })?.value.flatMap { $0.isEmpty ? nil : $0 }
        return (library, sort, genre)
    }
}

/// Home with nothing to show: no library yet, a first scan under way, or libraries with nothing recognised in them.
private struct EmptyHome: View {
    let message: EmptyHomeMessage

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Image(systemName: "film.stack")
                .font(.system(size: 44, weight: .semibold))
                .foregroundStyle(Color.silver)
            Text(message.title)
                .font(.marquee(34, weight: .black))
                .foregroundStyle(Color.screen)
            // A profile with an age limit gets the title alone (see `EmptyHomeMessage`).
            if !message.body.isEmpty {
                Text(message.body)
                    .font(.body)
                    .foregroundStyle(Color.usher)
                    .fixedSize(horizontal: false, vertical: true)
            }
            if !message.folders.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(message.folders, id: \.self) { folder in
                        Text(folder)
                            .font(.footnote.monospaced())
                            .foregroundStyle(Color.screen)
                    }
                }
                .accessibilityElement(children: .combine)
                .accessibilityLabel("Folders: \(message.folders.joined(separator: ", "))")
            }
            if !message.body.isEmpty {
                Text("Pull down to refresh.")
                    .font(.footnote)
                    .foregroundStyle(Color.usher)
            }
        }
        .padding(.horizontal, 24)
        .frame(maxWidth: 520, alignment: .leading)
        .frame(maxWidth: .infinity)
    }
}

private extension View {
    /// No iOS 26 blur across the top: the hero's artwork runs clear up to the status bar.
    @ViewBuilder
    func withoutTopEdgeEffect() -> some View {
        if #available(iOS 26, *) {
            scrollEdgeEffectHidden(true, for: .top)
        } else {
            self
        }
    }
}

/// What Sonarr and Radarr expect soon: episodes airing and movies coming out, with any download under way.
/// One already in the library (a show's next episode) opens it; See all opens the Coming soon page.
struct ComingSoonRow: View {
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(Router.self) private var router
    let items: [UpcomingItem]
    let downloading: Int

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: downloading > 0 ? "Coming soon (\(downloading) downloading)" : "Coming soon") { router.push(.comingSoon) }
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                    ForEach(items) { ComingSoonCard(item: $0, width: Layout.wideWidth(sizeClass)) }
                }
                .padding(.horizontal, Layout.edge)
            }
            .scrollIndicators(.hidden)
        }
    }
}

private struct ComingSoonCard: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let item: UpcomingItem
    let width: CGFloat
    @State private var showingAbout = false

    /// One in the library opens it; anything else opens what's known about it.
    var body: some View {
        Button {
            if let id = item.libraryItemId { router.push(.itemID(id)) } else { showingAbout = true }
        } label: { card }
        .buttonStyle(CardButtonStyle())
        .sheet(isPresented: $showingAbout) {
            UpcomingAboutSheet(item: item, detail: UpcomingDay.detail(item))
                .presentationDetents([.medium, .large])
                .presentationDragIndicator(.visible)
        }
    }

    private var card: some View {
        VStack(alignment: .leading, spacing: 6) {
            Color.seat
                .frame(width: width, height: width * 9 / 16)
                .overlay {
                    RemoteImage(url: model.client?.imageURL(item.still ?? item.poster, width: 640)) {
                        Text(item.seriesTitle ?? item.title)
                            .font(.footnote.weight(.semibold))
                            .foregroundStyle(Color.usher)
                            .multilineTextAlignment(.center)
                            .padding(10)
                    }
                }
                .overlay(alignment: .topLeading) {
                    Text(UpcomingDay.label(item.date))
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(Color.screen)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 4)
                        .background(Capsule().fill(.black.opacity(0.6)))
                        .padding(8)
                }
                .overlay(alignment: .bottom) {
                    if let progress = item.download?.progress {
                        ProgressBar(value: progress).padding(8)
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
            VStack(alignment: .leading, spacing: 1) {
                Text(UpcomingDay.title(item))
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Color.screen.opacity(0.9))
                Text(UpcomingDay.detail(item))
                    .font(.caption)
                    .foregroundStyle(Color.usher)
            }
            .lineLimit(1)
            .frame(width: width, alignment: .leading)
        }
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }
}
