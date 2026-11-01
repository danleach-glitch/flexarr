import FlexarrKit
import SwiftUI

/// Detail for a movie, series or episode (a season opens its series with that season picked).
/// The backdrop stays put, washed in the title's ambient colour, while the page scrolls over it.
struct DetailView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let id: String
    let preview: ItemSummary?

    @State private var detail: ItemDetail?
    @State private var error: String?
    @State private var nextUp: ItemSummary?
    @State private var similar: [ItemSummary] = []
    @State private var selectedSeasonID: String?
    @State private var seasons: [String: ItemDetail] = [:]
    @State private var scrollOffset: CGFloat = 0
    /// `model.refreshToken` when `detail` was last fetched.
    @State private var loadedToken: Int?
    @State private var loadedAt: Date?
    @State private var isVisible = false
    /// The More menu's choices, searching for subtitles for this file (its id), and what was saved, to say so.
    @State private var showingMore = false
    @State private var subtitleSearchFile: SubtitleSearchFile?
    @State private var savedSubtitles: String?
    /// Said once the search has gone (an alert can't come up while the search is still leaving).
    @State private var subtitlesJustSaved: String?
    @Environment(\.scenePhase) private var scenePhase
    @FocusState private var focusedSeason: String?
    @FocusState private var focusedEpisode: String?
    /// The round action button in focus, by name, to show it under the row.
    @FocusState private var focusedAction: String?

    private var item: ItemSummary? { detail?.item ?? (preview?.kind == .season ? nil : preview) }
    private var series: ItemSummary? {
        guard let item else { return nil }
        return item.kind == .series ? item : detail?.ancestors.first(where: { $0.kind == .series })
    }
    private var ambient: Color { item?.glowColor ?? .silver }

    var body: some View {
        ZStack {
            Color.room.ignoresSafeArea()
            if let item, item.kind.isMusic {
                // Opened by id (a deep link, say) and turned out to be music: its own screen takes over.
                MusicDestination(item: item)
            } else if let item {
                backdrop(for: item)
                ScrollViewReader { proxy in
                    ScrollView(.vertical) {
                        VStack(alignment: .leading, spacing: 56) {
                            header(item)
                                .frame(minHeight: 860, alignment: .bottomLeading)
                            sections(item)
                        }
                        .padding(.bottom, 90)
                    }
                    .onChange(of: focusedSeason) { _, id in
                        // Keep the episode list in view while choosing a season.
                        guard id != nil else { return }
                        withAnimation(.easeInOut(duration: 0.35)) { proxy.scrollTo("episodes", anchor: .bottom) }
                    }
                }
                .scrollClipDisabled()
                .onScrollGeometryChange(for: CGFloat.self) { $0.contentOffset.y } action: { _, y in
                    scrollOffset = y
                }
            } else if let error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .ignoresSafeArea(edges: [.top, .horizontal])
        .fullScreenCover(item: $subtitleSearchFile, onDismiss: {
            savedSubtitles = subtitlesJustSaved
            subtitlesJustSaved = nil
        }) { file in
            if let client = model.client, let item {
                SubtitleSearchView(client: client, itemId: item.id, fileId: file.id, onDownloaded: { saved in
                    subtitlesJustSaved = "\(saved.label) subtitles saved"
                    subtitleSearchFile = nil
                }, onClose: { subtitleSearchFile = nil })
                .presentationBackground(Color.black.opacity(0.45))
            }
        }
        .alert(savedSubtitles ?? "", isPresented: Binding(get: { savedSubtitles != nil }, set: { if !$0 { savedSubtitles = nil } })) {
            Button("OK", role: .cancel) {}
        }
        .task(id: model.refreshToken) {
            // Coming back from a screen pushed on top or another tab runs this again; fetch only when something
            // changed here, or what's shown is old enough that something may have changed elsewhere.
            guard loadedToken != model.refreshToken || Freshness.isStale(loadedAt) else { return }
            await load()
        }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible, Freshness.isStale(loadedAt) { Task { await load() } }
        }
    }

    // MARK: Backdrop

    private func backdrop(for item: ItemSummary) -> some View {
        let path = item.artwork.backdrop ?? series?.artwork.backdrop
        return ZStack {
            RemoteImage(url: model.client?.imageURL(path, width: 1920)) { Color.room }
                .frame(width: 1920, height: 1080)
                .clipped()
            // Ambient wash: the room takes the colour of the title.
            LinearGradient(colors: [ambient.opacity(0.55), ambient.opacity(0.1)], startPoint: .bottomLeading, endPoint: .topTrailing)
                .blendMode(.softLight)
            LinearGradient(stops: [.init(color: .room.opacity(0.95), location: 0), .init(color: .room.opacity(0.6), location: 0.45), .init(color: .clear, location: 0.8)],
                           startPoint: .leading, endPoint: .trailing)
            LinearGradient(stops: [.init(color: .clear, location: 0.35), .init(color: .room.opacity(0.9), location: 0.9), .init(color: .room, location: 1)],
                           startPoint: .top, endPoint: .bottom)
            RadialGradient(colors: [ambient.opacity(0.35), .clear], center: .bottomLeading, startRadius: 0, endRadius: 1100)
            // Dim further as the page scrolls so rows stay legible over the artwork.
            Color.room.opacity(min(0.85, max(0, scrollOffset) / 700))
        }
        .ignoresSafeArea()
        .animation(.easeOut(duration: 0.4), value: item.id)
    }

    // MARK: Header

    private func header(_ item: ItemSummary) -> some View {
        VStack(alignment: .leading, spacing: 22) {
            if item.kind == .episode, let series {
                TitleArt(item: series, maxWidth: 560, maxHeight: 170, fontSize: 80)
                Text(item.episodeSubtitle ?? item.title)
                    .font(.marquee(54, weight: .bold))
                    .foregroundStyle(Color.screen)
            } else {
                TitleArt(item: item, maxWidth: 820, maxHeight: 260, fontSize: 120)
            }

            HStack(spacing: 22) {
                FactsLine(facts: item.facts(scores: !model.isKids))
                if let genres = detail?.genres, !genres.isEmpty {
                    Text(genres.prefix(3).joined(separator: ", "))
                        .font(.system(size: 26, weight: .medium))
                        .foregroundStyle(Color.usher)
                }
            }

            if let overview = detail?.overview, !overview.isEmpty {
                Text(overview)
                    .font(.system(size: 27))
                    .lineSpacing(5)
                    .foregroundStyle(Color.screen.opacity(0.88))
                    .lineLimit(4)
                    .frame(maxWidth: 1040, alignment: .leading)
            }

            actions(item)
                .padding(.top, 14)
        }
        .padding(.leading, Layout.edge)
        .padding(.trailing, 300)
        .padding(.bottom, 10)
    }

    private func isWatched(_ item: ItemSummary) -> Bool { item.userData.watched || item.userData.unwatchedCount == 0 }

    /// The buttons in the order every client shares (`DetailActions`).
    private func detailActions(_ item: ItemSummary, playable: ItemSummary?) -> DetailActions {
        let kind: DetailActions.Kind = item.kind == .series ? .show : item.kind == .episode ? .episode : .movie
        return DetailActions(kind: kind, hasProgress: playable?.isResumable == true && item.kind != .series,
                             watched: isWatched(item), hasTrailer: trailer != nil, isKids: model.isKids,
                             platform: .tv, canPlayTrailers: canPlayTrailers, canDownload: false,
                             canSearchSubtitles: detail?.canSearchSubtitles == true && detail?.files.first != nil)
    }

    private var trailer: YouTubeTrailer? { YouTubeTrailer(id: detail?.trailerYouTubeId) }

    /// Trailers open in the YouTube app, so only when it's installed (youtube is in LSApplicationQueriesSchemes).
    private var canPlayTrailers: Bool { trailer.map { UIApplication.shared.canOpenURL($0.appURL) } ?? false }

    /// Play and Start over spelled out; the rest round icons, named underneath while focused.
    private func actions(_ item: ItemSummary) -> some View {
        let playable = playTarget(item)
        let actions = detailActions(item, playable: playable)
        return VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 26) {
                ForEach(actions.buttons, id: \.self) { button in
                    actionButton(button, item: item, playable: playable, menu: actions.menu)
                }
            }
            .focusSection()

            // The focused round button's name; the line keeps its height so nothing jumps.
            Text(focusedAction ?? " ")
                .font(.system(size: 24, weight: .medium))
                .foregroundStyle(Color.screen.opacity(0.75))
                .animation(.easeOut(duration: 0.15), value: focusedAction)
                .accessibilityHidden(true)
        }
    }

    @ViewBuilder
    private func actionButton(_ button: DetailActions.Button, item: ItemSummary, playable: ItemSummary?,
                              menu: [DetailActions.MenuItem]) -> some View {
        let watched = isWatched(item)
        switch button {
        case .play:
            if let playable {
                Button { model.play(playable) } label: {
                    Label(playLabel(playable, container: item), systemImage: "play.fill")
                }
                .buttonStyle(ActionButtonStyle(ambient: item.ambientColor ?? series?.ambientColor, prominent: true))
                .monospacedDigit()
                .takesFocusFromLink()
            }
        case .startOver:
            if let playable {
                Button { model.play(playable, fromBeginning: true) } label: {
                    Label("Start over", systemImage: "arrow.counterclockwise")
                }
                .buttonStyle(ActionButtonStyle(ambient: ambient))
            }
        case .shuffle:
            if item.childCount > 0 {
                roundButton("Shuffle", systemImage: "shuffle") { model.shuffle(item) }
            }
        case .trailer:
            roundButton("Trailer", systemImage: "film") { openTrailer() }
        case .watched:
            let name = watched ? "Mark as unwatched" : "Mark as watched"
            roundButton(name, systemImage: watched ? "eye.slash" : "checkmark") {
                Task { await model.setWatched(item, !watched) }
            }
        case .watchlist:
            // Only what the server can put on a watchlist: films and shows, from servers that have one.
            if model.hasWatchlist, item.kind == .movie || item.kind == .series, let onList = detail?.onWatchlist {
                WatchlistActionButton(key: .library(item.id), on: onList, ambient: ambient, iconOnly: true)
                    .focused($focusedAction, equals: "Watchlist")
            }
        case .download:
            EmptyView()
        case .more:
            // Things done now and then, out of the way of the row above.
            roundButton("More", systemImage: "ellipsis") { showingMore = true }
                .confirmationDialog(item.title, isPresented: $showingMore) {
                    ForEach(menu, id: \.self) { entry in
                        switch entry {
                        case .markUnwatched:
                            Button("Mark as unwatched") { Task { await model.setWatched(item, false) } }
                        case .subtitles:
                            if let file = detail?.files.first {
                                Button("Search for subtitles…") { subtitleSearchFile = SubtitleSearchFile(id: file.id) }
                            }
                        case .goToShow:
                            if let series {
                                Button("Go to show") { router.push(.item(series)) }
                            }
                        }
                    }
                }
        }
    }

    /// A round icon button, named under the row while it's focused.
    private func roundButton(_ name: String, systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) { Label(name, systemImage: systemImage) }
            .buttonStyle(ActionButtonStyle(ambient: ambient, iconOnly: true))
            .focused($focusedAction, equals: name)
    }

    private func openTrailer() {
        guard let trailer else { return }
        UIApplication.shared.open(trailer.appURL)
    }

    /// What Play starts: the item itself, or for a series the next-up episode (falling
    /// back to the first episode of the selected season).
    private func playTarget(_ item: ItemSummary) -> ItemSummary? {
        switch item.kind {
        case .movie, .episode: item
        case .series: nextUp ?? selectedSeason?.children.first
        default: nil
        }
    }

    private func playLabel(_ target: ItemSummary, container: ItemSummary) -> String {
        let resume = target.isResumable
        if container.kind == .series, let code = target.episodeCode {
            return resume ? "Resume \(code)" : "Play \(code)"
        }
        return resume ? "Resume from \(Format.timestamp(target.userData.positionSeconds))" : "Play"
    }

    // MARK: Sections

    @ViewBuilder
    private func sections(_ item: ItemSummary) -> some View {
        if let detail {
            if item.kind == .series, !detail.children.isEmpty {
                seasonsSection(detail.children)
            } else if item.kind == .episode, let season = selectedSeason, season.children.count > 1 {
                episodesRow(title: season.item.title, season: season)
            }

            let cast = detail.credits.sorted { rank($0.role) < rank($1.role) }
            if !cast.isEmpty {
                VStack(alignment: .leading, spacing: 0) {
                    RowHeading(title: "Cast and crew")
                    ScrollView(.horizontal) {
                        LazyHStack(alignment: .top, spacing: 36) {
                            ForEach(Array(cast.prefix(30).enumerated()), id: \.offset) { _, credit in
                                PersonCard(name: credit.name, detail: credit.character ?? credit.role.rawValue, image: credit.image) {
                                    router.push(.person(id: credit.personId, name: credit.name))
                                }
                            }
                        }
                        .padding(.horizontal, Layout.edge)
                        .padding(.vertical, 30)
                    }
                    .scrollClipDisabled()
                    .focusSection()
                }
            }

            if !similar.isEmpty {
                VStack(alignment: .leading, spacing: 0) {
                    RowHeading(title: "More like this")
                    ScrollView(.horizontal) {
                        LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                            ForEach(similar) { other in
                                PosterCard(item: other) { router.push(.item(other)) }
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

            if let file = detail.files.first {
                fileInfo(file)
            }
        }
    }

    private func seasonsSection(_ seasonList: [ItemSummary]) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            ScrollView(.horizontal) {
                HStack(spacing: 18) {
                    ForEach(seasonList) { season in
                        Button(season.title) { selectedSeasonID = season.id }
                            .buttonStyle(PillButtonStyle(selected: season.id == selectedSeasonID, ambient: ambient))
                            .focused($focusedSeason, equals: season.id)
                    }
                }
                .padding(.horizontal, Layout.edge)
                .padding(.vertical, 20)
            }
            .scrollClipDisabled()
            .focusSection()
            .defaultFocus($focusedSeason, selectedSeasonID, priority: .userInitiated)
            .onChange(of: focusedSeason) { _, id in
                // Like the TV app: moving across the seasons switches the episode list.
                if let id { selectedSeasonID = id }
            }

            if let season = selectedSeason {
                episodesList(season)
            } else {
                ProgressView().padding(.leading, Layout.edge).frame(height: 420)
            }
        }
        .id("episodes")
        .task(id: selectedSeasonID) { await loadSelectedSeason() }
    }

    private func episodesRow(title: String, season: ItemDetail) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            RowHeading(title: title)
            episodesList(season)
        }
    }

    private func episodesList(_ season: ItemDetail) -> some View {
        // The episode to land on: the one being viewed, else next up, else the first unwatched.
        let anchor = [item?.kind == .episode ? item?.id : nil, nextUp?.id, season.children.first(where: { !$0.userData.watched })?.id]
            .compactMap { $0 }
            .first { id in season.children.contains { $0.id == id } }
        return ScrollViewReader { proxy in
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: 40) {
                    ForEach(season.children) { episode in
                        EpisodeCard(episode: episode, overview: season.childOverviews[episode.id] ?? nil,
                                    isCurrent: episode.id == item?.id, focus: $focusedEpisode) {
                            if episode.id == item?.id || item?.kind == .series {
                                model.play(episode)
                            } else {
                                router.push(.item(episode))
                            }
                        }
                        .id(episode.id)
                    }
                }
                .padding(.top, 24)
                .padding(.bottom, 40)
            }
            .contentMargins(.horizontal, Layout.edge, for: .scrollContent)
            .scrollClipDisabled()
            .focusSection()
            .defaultFocus($focusedEpisode, anchor, priority: .userInitiated)
            .onChange(of: focusedEpisode) { old, new in
                // Entering the row from above or below lands on the anchor episode, not
                // whichever card happens to sit under the previous focus.
                if old == nil, let new, let anchor, new != anchor { focusedEpisode = anchor }
            }
            .onAppear {
                if let anchor { proxy.scrollTo(anchor, anchor: .leading) }
            }
            .onChange(of: season.item.id) {
                if let anchor { proxy.scrollTo(anchor, anchor: .leading) }
            }
        }
    }

    private func fileInfo(_ file: MediaFile) -> some View {
        let video = [file.height.map { "\($0)p" }, file.videoCodec?.uppercased(), file.pictureLabel]
            .compactMap { $0 }.joined(separator: " ")
        let audio = [file.audioCodec?.uppercased(), file.audioChannels.map(AudioChannels.label)].compactMap { $0 }.joined(separator: " ")
        return HStack(spacing: 50) {
            if !video.isEmpty { labeled("Video", video) }
            if !audio.isEmpty { labeled("Audio", audio) }
            if let studio = detail?.studio { labeled("Studio", studio) }
            labeled("Size", ByteCountFormatter.string(fromByteCount: file.size, countStyle: .file))
        }
        .padding(.horizontal, Layout.edge)
    }

    private func labeled(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label).font(.system(size: 21, weight: .semibold)).foregroundStyle(Color.usher)
            Text(value).font(.system(size: 25)).foregroundStyle(Color.screen.opacity(0.85))
        }
    }

    private func rank(_ role: CreditRole) -> Int {
        switch role {
        case .actor: 0
        case .creator: 1
        case .director: 2
        case .writer: 3
        default: 4
        }
    }

    private var selectedSeason: ItemDetail? {
        selectedSeasonID.flatMap { seasons[$0] }
    }

    // MARK: Loading

    private func load() async {
        guard let client = model.client else { return }
        let token = model.refreshToken
        do {
            var loaded = try await client.item(id)
            // A season opens as its series, with that season selected.
            if loaded.item.kind == .season, let parent = loaded.item.parentId {
                seasons[loaded.item.id] = loaded
                if selectedSeasonID == nil { selectedSeasonID = loaded.item.id }
                loaded = try await client.item(parent)
            }
            detail = loaded
            loadedToken = token
            loadedAt = Date()
            error = nil

            let item = loaded.item
            switch item.kind {
            case .series:
                nextUp = try? await client.nextUp(item.id)
                if selectedSeasonID == nil {
                    selectedSeasonID = nextUp?.parentId ?? loaded.children.first?.id
                }
            case .episode:
                if let seasonID = item.parentId {
                    selectedSeasonID = seasonID
                }
            default:
                break
            }
            await loadSelectedSeason(force: true)
            if similar.isEmpty, item.kind != .episode {
                similar = (try? await client.similar(item.id)) ?? []
            }
        } catch {
            if detail == nil { self.error = ConnectionError(error).message }
        }
    }

    private func loadSelectedSeason(force: Bool = false) async {
        guard let client = model.client, let id = selectedSeasonID else { return }
        if !force, seasons[id] != nil { return }
        if let season = try? await client.item(id) {
            seasons[id] = season
        }
    }
}

/// An episode in a season list: still with progress, then number, title and overview.
struct EpisodeCard: View {
    @Environment(AppModel.self) private var model
    let episode: ItemSummary
    let overview: String?
    var isCurrent = false
    var focus: FocusState<String?>.Binding?
    let action: () -> Void

    private let width: CGFloat = 440

    var body: some View {
        ArtworkButton(ambient: episode.glowColor, focus: focus, focusID: episode.id, action: action) {
            RemoteImage(url: model.client?.imageURL(episode.artwork.thumb ?? episode.artwork.backdrop, width: 640)) {
                ZStack {
                    Color.seat
                    Image(systemName: "play.fill").font(.system(size: 40)).foregroundStyle(Color.usher)
                }
            }
            .frame(width: width, height: width * 9 / 16)
            .overlay(alignment: .bottom) {
                if let progress = episode.progress {
                    ZStack(alignment: .bottom) {
                        LinearGradient(colors: [.clear, .black.opacity(0.55)], startPoint: .top, endPoint: .bottom).frame(height: 80)
                        ProgressBar(value: progress).padding(14)
                    }
                }
            }
            .overlay(alignment: .topTrailing) {
                if episode.userData.watched { WatchedBadge().padding(12) }
            }
            .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
        } caption: { focused in
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 14) {
                    Text(episode.episodeNumber.map { "Episode \($0)" } ?? "Special")
                    if let time = timeText { Text(time) }
                }
                .font(.system(size: 20, weight: .semibold))
                .foregroundStyle(isCurrent ? Color.screen : Color.usher)
                Text(episode.title)
                    .font(.system(size: 25, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.88))
                    .lineLimit(1)
                if let overview, !overview.isEmpty {
                    Text(overview)
                        .font(.system(size: 20))
                        .foregroundStyle(Color.usher)
                        .lineLimit(3)
                        .lineSpacing(2)
                }
            }
            .frame(width: width, alignment: .leading)
        }
        .contextMenu { ItemContextMenu(item: episode, showsDetails: true) }
    }

    private var timeText: String? {
        if episode.progress != nil, let duration = episode.effectiveDuration {
            return Format.remaining(duration - episode.userData.positionSeconds)
        }
        return episode.runtimeMinutes.flatMap(Format.runtime(minutes:)) ?? episode.durationSeconds.flatMap(Format.runtime(seconds:))
    }
}

