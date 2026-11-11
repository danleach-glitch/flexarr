import FlexarrKit
import SwiftUI

/// Detail for a movie, series or episode (a season opens its series with that season picked). The backdrop sits
/// at the top, washed in the title's ambient colour, with the actions and episodes beneath it.
struct DetailView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.verticalSizeClass) private var verticalSizeClass
    let id: String
    let preview: ItemSummary?

    @State private var detail: ItemDetail?
    /// The episode shown now, once watching from this page has moved it on from `id`.
    @State private var shownID: String?
    /// Given to the player with Play, so this page knows the sitting was started here (`takeEnding`).
    @State private var pageToken = UUID().uuidString
    @State private var error: String?
    @State private var nextUp: ItemSummary?
    @State private var similar: [ItemSummary] = []
    @State private var selectedSeasonID: String?
    @State private var seasons: [String: ItemDetail] = [:]
    @State private var overviewExpanded = false
    /// `model.refreshToken` when `detail` was last fetched.
    @State private var loadedToken: Int?
    @State private var loadedAt: Date?
    @State private var isVisible = false
    /// Searching for subtitles for this file (its id), and what was saved, to say so.
    @State private var subtitleSearchFile: SubtitleSearchFile?
    @State private var savedSubtitles: String?
    /// Said once the search has gone, so the banner shows on the page rather than under the leaving sheet.
    @State private var subtitlesJustSaved: String?
    /// On the watchlist or not, as changed here, until the page next loads.
    @State private var onWatchlist: Bool?
    @State private var showingTrailer = false
    /// A show's seasons and episodes the library lacks, and what's coming.
    @State private var gaps: ShowGaps = .none
    /// Fetching `gaps`, on its own so a slow Sonarr or Seerr never holds up the page or a pull to refresh.
    @State private var gapsTask: Task<Void, Never>?
    @Environment(\.scenePhase) private var scenePhase

    private var item: ItemSummary? { detail?.item ?? (preview?.kind == .season ? nil : preview) }
    /// What Play would start here, got ready after a moment on the page.
    private var warmTarget: ItemSummary? { item.flatMap(playTarget) }
    private var series: ItemSummary? {
        guard let item else { return nil }
        return item.kind == .series ? item : detail?.ancestors.first(where: { $0.kind == .series })
    }
    private var ambient: Color { item?.glowColor ?? .silver }
    /// Shorter on a phone held sideways, so the title and Play stay on screen.
    private var heroHeight: CGFloat { verticalSizeClass == .compact ? 280 : sizeClass == .regular ? 520 : 420 }

    var body: some View {
        ZStack {
            RoomBackground(ambient: ambient, intensity: 0.22)
            if let item, item.kind.isMusic {
                // Opened by id (a deep link, say) and turned out to be music: its own screen takes over.
                MusicDestination(item: item)
            } else if let item, item.kind == .audiobook {
                // Opened by id and turned out to be a book: its own page.
                BookView(id: item.id, preview: item)
            } else if let item {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 28) {
                        header(item)
                        sections(item)
                    }
                    .padding(.bottom, 40)
                }
                .ignoresSafeArea(edges: .top)
                .refreshable { await load() }
            } else if let error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(.hidden, for: .navigationBar)
        .sheet(item: $subtitleSearchFile, onDismiss: {
            savedSubtitles = subtitlesJustSaved
            subtitlesJustSaved = nil
        }) { file in
            if let client = model.client, let item {
                SubtitleSearchView(client: client, itemId: item.id, fileId: file.id, onDownloaded: { saved in
                    subtitlesJustSaved = t("{language} subtitles saved", ["language": saved.label])
                    subtitleSearchFile = nil
                }, onClose: { subtitleSearchFile = nil })
                .presentationDetents([.medium, .large])
                .presentationDragIndicator(.visible)
            }
        }
        .sheet(isPresented: $showingTrailer) {
            if let trailer, let item {
                TrailerSheet(trailer: trailer, title: item.title)
            }
        }
        .banner($savedSubtitles)
        .task(id: model.refreshToken) {
            // Coming back from a screen pushed on top or another tab runs this again; fetch only when something
            // changed here, or what's shown is old enough that something may have changed elsewhere.
            guard loadedToken != model.refreshToken || Freshness.isStale(loadedAt) else { return }
            await load()
        }
        .onAppear { isVisible = true }
        .onDisappear {
            isVisible = false
            if let warmTarget { model.dropReady(warmTarget) }
        }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible, Freshness.isStale(loadedAt) { Task { await load() } }
            if phase == .background, let warmTarget { model.dropReady(warmTarget) }
        }
        .task(id: warmTarget?.id) {
            // Looked at for a moment: what Play would start is got ready, so it opens at once (docs/fast-start.md).
            guard let warmTarget, (try? await Task.sleep(for: .seconds(2))) != nil else { return }
            model.getReady(warmTarget)
        }
    }

    // MARK: Header

    private func header(_ item: ItemSummary) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            ZStack(alignment: .bottomLeading) {
                backdrop(for: item)
                VStack(alignment: .leading, spacing: 8) {
                    if item.kind == .episode, let series {
                        TitleArt(item: series, maxWidth: 240, maxHeight: 80, fontSize: 40)
                        Text(item.episodeSubtitle ?? item.title)
                            .font(.marquee(28, weight: .bold))
                            .foregroundStyle(Color.screen)
                    } else {
                        TitleArt(item: item, maxWidth: 300, maxHeight: 120, fontSize: 52)
                    }
                    FactsLine(facts: item.facts(scores: !model.isKids, versions: detail?.versions), scores: model.isKids ? nil : item.scores)
                    if let genres = detail?.genres, !genres.isEmpty {
                        Text(genres.prefix(3).joined(separator: ", "))
                            .font(.subheadline.weight(.medium))
                            .foregroundStyle(Color.usher)
                    }
                }
                .padding(.horizontal, Layout.edge)
            }

            actions(item)
                .padding(.horizontal, Layout.edge)

            if let overview = detail?.overview, !overview.isEmpty {
                Text(overview)
                    .font(.subheadline)
                    .lineSpacing(3)
                    .foregroundStyle(Color.screen.opacity(0.88))
                    .lineLimit(overviewExpanded ? nil : 4)
                    .frame(maxWidth: 680, alignment: .leading)
                    .padding(.horizontal, Layout.edge)
                    .contentShape(Rectangle())
                    .onTapGesture { withAnimation(.easeInOut(duration: 0.2)) { overviewExpanded.toggle() } }
                    .accessibilityAddTraits(.isButton)
                    .accessibilityHint(overviewExpanded ? t("Shows less") : t("Shows the whole overview"))
            }
        }
    }

    private func backdrop(for item: ItemSummary) -> some View {
        let path = item.artwork.backdrop ?? series?.artwork.backdrop
        return Color.clear
            .frame(height: heroHeight)
            .frame(maxWidth: .infinity)
            .overlay {
                RemoteImage(url: model.client?.imageURL(path, width: sizeClass == .regular ? 1600 : 1000)) { Color.room }
            }
            .clipped()
            .overlay {
                LinearGradient(colors: [ambient.opacity(0.5), ambient.opacity(0.1)], startPoint: .bottomLeading, endPoint: .topTrailing)
                    .blendMode(.softLight)
                LinearGradient(stops: [.init(color: .clear, location: 0.3), .init(color: .room.opacity(0.6), location: 1)],
                               startPoint: .top, endPoint: .bottom)
            }
            // Fade out rather than painting the room colour, so there's no seam with the ambient wash behind.
            .mask(LinearGradient(stops: [.init(color: .black, location: 0.55), .init(color: .clear, location: 1)], startPoint: .top, endPoint: .bottom))
    }

    /// The buttons in the order every client shares (`DetailActions`).
    private func detailActions(_ item: ItemSummary, playable: ItemSummary?) -> DetailActions {
        let kind: DetailActions.Kind = item.kind == .series ? .show : item.kind == .episode ? .episode : .movie
        return DetailActions(kind: kind, hasProgress: playable?.isResumable == true && item.kind != .series,
                             watched: isWatched(item), hasTrailer: trailer != nil, isKids: model.isKids,
                             platform: .phone, canPlayTrailers: canPlayTrailers,
                             canDownload: item.kind == .movie || item.kind == .episode || item.kind == .series,
                             canSearchSubtitles: detail?.canSearchSubtitles == true && detail?.files.first != nil,
                             canWatchlist: watchlistState(item) != nil,
                             canChooseVersion: (detail?.versions?.count ?? 0) > 1,
                             wide: sizeClass == .regular)
    }

    /// Whether it's on the watchlist, or nil when it can't go on one here: only films and shows, from servers that have one.
    private func watchlistState(_ item: ItemSummary) -> Bool? {
        guard model.hasWatchlist, item.kind == .movie || item.kind == .series else { return nil }
        return onWatchlist ?? detail?.onWatchlist
    }

    private func setOnWatchlist(_ item: ItemSummary, _ on: Bool) {
        Task { if await model.setOnWatchlist(.library(item.id), on) != nil { onWatchlist = on } }
    }

    private func isWatched(_ item: ItemSummary) -> Bool { item.userData.watched || item.userData.unwatchedCount == 0 }

    private var trailer: YouTubeTrailer? { YouTubeTrailer(id: detail?.trailerYouTubeId) }

    /// Trailers play in a sheet on the phone, wherever there's one.
    private var canPlayTrailers: Bool { true }

    @ViewBuilder
    private func actions(_ item: ItemSummary) -> some View {
        let playable = playTarget(item)
        let watched = isWatched(item)
        let actions = detailActions(item, playable: playable)
        let wide = sizeClass == .regular
        let play = playable.map { playable in
            Button { model.play(playable, from: pageToken) } label: {
                Label(playLabel(playable, container: item), systemImage: Symbol.play)
                    .frame(maxWidth: wide ? nil : .infinity)
                    .frame(minWidth: wide ? 200 : nil)
            }
            .buttonStyle(ActionButtonStyle(ambient: item.ambientColor ?? series?.ambientColor, prominent: true, singleLine: wide))
            .monospacedDigit()
        }
        let others = HStack(alignment: .top, spacing: wide ? 4 : 8) {
            ForEach(actions.buttons.filter { $0 != .play }, id: \.self) { button in
                secondaryButton(button, item: item, playable: playable, watched: watched, menu: actions.menu)
            }
        }
        .buttonStyle(LabeledActionStyle(packed: wide))
        if wide {
            // An iPad or a Mac: Play at its own width with the rest beside it, as on the web.
            HStack(alignment: .top, spacing: 20) {
                play
                others
            }
        } else {
            // Play on a line of its own, so "Resume from 1:02:35" never wraps; the rest underneath, named.
            VStack(spacing: 18) {
                play
                others
            }
            .frame(maxWidth: 560, alignment: .leading)
        }
    }

    @ViewBuilder
    private func secondaryButton(_ button: DetailActions.Button, item: ItemSummary, playable: ItemSummary?, watched: Bool,
                                 menu: [DetailActions.MenuItem]) -> some View {
        switch button {
        case .play:
            EmptyView()
        case .startOver:
            if let playable {
                Button { model.play(playable, fromBeginning: true, from: pageToken) } label: {
                    Label(t(Words.startOver), systemImage: Symbol.startOver)
                }
            }
        case .shuffle:
            if item.childCount > 0 {
                Button { model.shuffle(item) } label: { Label(t(Words.shuffle), systemImage: Symbol.shuffle) }
            }
        case .trailer:
            Button { showingTrailer = true } label: { Label(t(Words.trailer), systemImage: Symbol.trailer) }
        case .watched:
            // A toggle like Download: the tick fills once it's watched, and the name stays put.
            Button { Task { await model.setWatched(item, !watched) } } label: {
                Label(t("Watched"), systemImage: watched ? Symbol.isWatched : Symbol.watched)
            }
            .accessibilityLabel(watched ? t(Words.markAsUnwatched) : t(Words.markAsWatched))
            .accessibilityAddTraits(watched ? .isSelected : [])
        case .watchlist:
            if let onList = watchlistState(item) {
                Button { setOnWatchlist(item, !onList) } label: {
                    Label(t(Words.watchlist), systemImage: onList ? Symbol.onWatchlist : Symbol.watchlist)
                }
                .accessibilityLabel(onList ? t(Words.removeFromWatchlist) : t(Words.addToWatchlist))
            }
        case .download:
            if item.kind == .series {
                // Every episode of every season.
                ShowDownloadButton(show: item, seasons: (detail?.children ?? []).filter { $0.kind == .season })
            } else {
                DownloadButton(item: item, copies: detail?.copyIds ?? [])
            }
        case .more:
            Menu {
                ForEach(menu, id: \.self) { entry in
                    switch entry {
                    case .button(.shuffle):
                        if item.childCount > 0 {
                            Button { model.shuffle(item) } label: { Label(t(Words.shuffle), systemImage: Symbol.shuffle) }
                        }
                    case .button(.trailer):
                        Button { showingTrailer = true } label: { Label(t(Words.trailer), systemImage: Symbol.trailer) }
                    case .button(.watched):
                        Button { Task { await model.setWatched(item, !watched) } } label: {
                            Label(watched ? t(Words.markAsUnwatched) : t(Words.markAsWatched),
                                  systemImage: watched ? Symbol.markUnwatched : Symbol.watched)
                        }
                    case .button(.watchlist):
                        if let onList = watchlistState(item) {
                            Button { setOnWatchlist(item, !onList) } label: {
                                Label(onList ? t(Words.removeFromWatchlist) : t(Words.addToWatchlist),
                                      systemImage: onList ? Symbol.offWatchlist : Symbol.watchlist)
                            }
                        }
                    case .button:
                        EmptyView()
                    case .version:
                        versionMenu(item)
                    case .subtitles:
                        if let file = detail?.subtitleTargetId {
                            Button { subtitleSearchFile = SubtitleSearchFile(id: file) } label: {
                                Label(t("Search for subtitles"), systemImage: Symbol.subtitles)
                            }
                        }
                    case .goToShow:
                        if let series {
                            Button { router.push(.item(series)) } label: { Label(t(Words.goToShow), systemImage: Symbol.goToShow) }
                        }
                    }
                }
            } label: {
                Label(t("More"), systemImage: Symbol.more)
            }
            .accessibilityLabel(t("More actions"))
        }
    }

    private func chooseVersion(_ item: ItemSummary, _ fileId: String?) {
        Task {
            if await !model.chooseVersion(item, fileId: fileId) { savedSubtitles = t("That didn’t work. Try again.") }
        }
    }

    /// Automatic first, then each version, with a check on what's chosen.
    private func versionMenu(_ item: ItemSummary) -> some View {
        let versions = detail?.versions ?? []
        return Menu {
            Picker(selection: Binding(get: { versions.first(where: \.chosen)?.fileId }, set: { fileId in
                chooseVersion(item, fileId)
            })) {
                Text(t(Words.automatic)).tag(String?.none)
                ForEach(versions) { version in
                    Text(version.name(among: versions)).tag(String?.some(version.fileId))
                }
            } label: {
                EmptyView()
            }
            .pickerStyle(.inline)
        } label: {
            Label(t(Words.version), systemImage: Symbol.version)
        }
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
            return resume ? t(Words.resumeEpisode, ["episode": code]) : t("Play {episode}", ["episode": code])
        }
        return resume ? t(Words.resumeFrom, ["time": Format.timestamp(target.userData.positionSeconds)]) : t(Words.play)
    }

    // MARK: Sections

    @ViewBuilder
    private func sections(_ item: ItemSummary) -> some View {
        if let detail {
            if item.kind == .series, !detail.children.isEmpty {
                seasonsSection(detail.children)
            }
            if item.kind == .series, gaps.hasMissing || !gaps.upcoming.isEmpty {
                ShowGapsSection(showId: item.id, tmdbId: detail.tmdbId.flatMap { Int($0) }, gaps: $gaps,
                                posters: ShowGaps.seasonPosters(detail.children), showPoster: item.artwork.poster)
            }
            if item.kind == .episode, let season = selectedSeason, season.children.count > 1 {
                VStack(alignment: .leading, spacing: 10) {
                    SectionHeading(title: season.item.title)
                    episodesList(season)
                }
            }

            let cast = detail.credits.sorted { rank($0.role) < rank($1.role) }
            if !cast.isEmpty {
                VStack(alignment: .leading, spacing: 10) {
                    SectionHeading(title: t(Words.castAndCrew))
                    ScrollView(.horizontal) {
                        LazyHStack(alignment: .top, spacing: 8) {
                            ForEach(Array(cast.prefix(30).enumerated()), id: \.offset) { _, credit in
                                PersonCard(id: credit.personId, name: credit.name, detail: credit.character ?? credit.role.rawValue, image: credit.image)
                            }
                        }
                        .padding(.horizontal, Layout.edge)
                    }
                    .scrollIndicators(.hidden)
                }
            }

            if !similar.isEmpty {
                VStack(alignment: .leading, spacing: 10) {
                    SectionHeading(title: t(Words.moreLikeThis))
                    ScrollView(.horizontal) {
                        LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                            ForEach(similar) { other in
                                PosterCard(item: other, width: Layout.posterWidth(sizeClass))
                            }
                        }
                        .padding(.horizontal, Layout.edge)
                    }
                    .scrollIndicators(.hidden)
                }
            }

            if let file = detail.files.first {
                fileInfo(file)
            }
        }
    }

    private func seasonsSection(_ seasonList: [ItemSummary]) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            if seasonList.count > 1 {
                // The pills scroll; Download Season N stays put at the end of their line.
                HStack(spacing: 12) {
                ScrollViewReader { proxy in
                    ScrollView(.horizontal) {
                        HStack(spacing: 8) {
                            ForEach(seasonList) { season in
                                Button { selectedSeasonID = season.id } label: { seasonPillLabel(season) }
                                    .buttonStyle(PillButtonStyle(selected: season.id == selectedSeasonID, ambient: ambient))
                                    .id(season.id)
                            }
                        }
                        .padding(.horizontal, Layout.edge)
                    }
                    .scrollIndicators(.hidden)
                    .onAppear { if let selectedSeasonID { proxy.scrollTo(selectedSeasonID, anchor: .center) } }
                }
                    seasonDownloadLink
                }
            } else if let only = seasonList.first {
                SectionHeading(title: gaps.countLabel(season: only.seasonNumber).map { "\(only.title): \($0)" } ?? only.title)
                    .overlay(alignment: .trailing) { seasonDownloadLink }
            }

            if let season = selectedSeason {
                episodesList(season)
            } else {
                ProgressView().frame(maxWidth: .infinity).frame(height: 200)
            }
        }
        .task(id: selectedSeasonID) { await loadSelectedSeason() }
    }

    /// Download Season N, at the end of the seasons' line, once the season's episodes are here.
    @ViewBuilder
    private var seasonDownloadLink: some View {
        if let season = selectedSeason {
            SeasonDownloadLink(season: season)
                .padding(.trailing, Layout.edge)
                .fixedSize()
        }
    }

    /// "Season 1", with a quieter "8 of 10" beside it when some of its aired episodes aren't in the library (as on the web).
    private func seasonPillLabel(_ season: ItemSummary) -> some View {
        HStack(spacing: 6) {
            Text(season.title)
            if let count = gaps.countLabel(season: season.seasonNumber) {
                Text(count).font(.footnote.weight(.medium)).opacity(0.7)
            }
        }
    }

    private func episodesList(_ season: ItemDetail) -> some View {
        // One column everywhere: on an iPad or a Mac each episode is a wide row, the overview beside its picture.
        let columns = [GridItem(.flexible(), alignment: .top)]
        return LazyVGrid(columns: columns, alignment: .leading, spacing: sizeClass == .regular ? 24 : 16) {
            ForEach(season.children) { episode in
                EpisodeRow(episode: episode, overview: season.childOverviews[episode.id] ?? nil,
                           isCurrent: episode.isSameEpisode(as: item)) {
                    if episode.isSameEpisode(as: item) || item?.kind == .series {
                        model.play(episode, from: pageToken)
                    } else {
                        router.push(.item(episode))
                    }
                }
            }
        }
        .padding(.horizontal, Layout.edge)
    }

    private func fileInfo(_ file: MediaFile) -> some View {
        // With versions, the one that plays or is chosen, which may be in another copy than the one opened.
        let version = detail?.shownVersion
        let video = version.map { [$0.label, $0.videoCodec?.uppercased()].compactMap { $0 }.joined(separator: " ") }
            ?? [file.height.map { "\($0)p" }, file.videoCodec?.uppercased(), file.pictureLabel].compactMap { $0 }.joined(separator: " ")
        let audioCodec = version == nil ? file.audioCodec : version?.audioCodec
        let channels = version == nil ? file.audioChannels : version?.audioChannels
        let audio = [audioCodec?.uppercased(), channels.map(AudioChannels.label)].compactMap { $0 }.joined(separator: " ")
        return LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), alignment: .topLeading)], alignment: .leading, spacing: 14) {
            if !video.isEmpty { labeled(t("Video"), video) }
            if !audio.isEmpty { labeled(t("Audio"), audio) }
            if let studio = detail?.studio { labeled(t("Studio"), studio) }
            labeled(t("Size"), formatBytes(version?.size ?? file.size))
        }
        .padding(.horizontal, Layout.edge)
    }

    private func labeled(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption.weight(.semibold)).foregroundStyle(Color.usher)
            Text(value).font(.subheadline).foregroundStyle(Color.screen.opacity(0.85))
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
            // Back from watching an episode started here: on to the next one (or the one it stopped part way through),
            // settled before anything is fetched, so a reload that cuts in can't lose it.
            let before = shownID
            if let target = model.player.takeEnding(from: pageToken)?.moveOn(from: shownID ?? id) { shownID = target }
            var loaded: ItemDetail
            do {
                loaded = try await client.item(shownID ?? id)
            } catch where shownID != before && !(error is CancellationError) {
                shownID = before // The next episode couldn't be had (gone, or not for this profile): stay.
                loaded = try await client.item(shownID ?? id)
            }
            if shownID != before, shownID == loaded.item.id {
                AccessibilityNotification.Announcement(t("Now showing {title}", ["title": [loaded.item.episodeCode, loaded.item.title].compactMap { $0 }.joined(separator: ", ")])).post()
            }
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
            // After the page, on their own: Sonarr or Seerr being slow never holds up the season, More like this
            // or the end of a reload.
            gapsTask?.cancel()
            if item.kind == .series, !model.isKids {
                let showId = item.id
                gapsTask = Task {
                    guard let shown = await Self.fetchGaps(client, showId), !Task.isCancelled, detail?.item.id == showId else { return }
                    gaps = shown
                }
            }
            // Beside the season, not after it.
            let similarClient = similar.isEmpty && item.kind != .episode ? client : nil
            async let shownSimilar = Self.fetchSimilar(similarClient, item.id)
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
            if let shown = await shownSimilar { similar = shown }
        } catch {
            if detail == nil { self.error = ConnectionError(error).message }
        }
    }

    /// What the show is missing, or nil without a client (not a show, or a kids' profile) or when it can't be had.
    nonisolated private static func fetchGaps(_ client: FlexarrClient?, _ showId: String) async -> ShowGaps? {
        guard let client else { return nil }
        return try? await client.showGaps(showId)
    }

    /// More like this, or nil without a client (already shown, or an episode) or when it can't be had.
    nonisolated private static func fetchSimilar(_ client: FlexarrClient?, _ id: String) async -> [ItemSummary]? {
        guard let client else { return nil }
        return try? await client.similar(id)
    }

    private func loadSelectedSeason(force: Bool = false) async {
        guard let client = model.client, let id = selectedSeasonID else { return }
        if !force, seasons[id] != nil { return }
        if let season = try? await client.item(id) {
            seasons[id] = season
        }
    }
}

/// An episode in a season list: still with progress beside the number, title and time, then the overview.
struct EpisodeRow: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    let episode: ItemSummary
    let overview: String?
    var isCurrent = false
    let action: () -> Void

    var body: some View {
        // The download button sits outside the row's own button, so pressing it never plays the episode.
        HStack(alignment: sizeClass == .regular ? .center : .top, spacing: 8) {
            Button(action: action) {
                if sizeClass == .regular { wide } else { narrow }
            }
            .buttonStyle(CardButtonStyle())
            .contextMenu { ItemContextMenu(item: episode, showsDetails: true) }
            DownloadButton(item: episode)
                .labelStyle(.iconOnly)
                .buttonStyle(.plain)
                .foregroundStyle(Color.screen)
                .frame(width: 44, height: 44)
                .contentShape(Rectangle())
                // A phone: level with the middle of the picture.
                .padding(.top, sizeClass == .regular ? 0 : 140 * 9 / 16 / 2 - 22)
        }
    }

    /// An iPad or a Mac: the picture, then the episode's number, title, length and overview beside it.
    private var wide: some View {
        HStack(alignment: .top, spacing: 20) {
            thumbnail(width: 260)
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 10) {
                    Text(episode.episodeNumber.map { t("Episode {number}", ["number": $0]) } ?? t("Special"))
                        .foregroundStyle(isCurrent ? Color.screen : Color.usher)
                    if let time = timeText { Text(time).foregroundStyle(Color.usher) }
                }
                .font(.subheadline.weight(.semibold))
                Text(episode.title)
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(Color.screen)
                    .lineLimit(2)
                if let overview, !overview.isEmpty {
                    Text(overview)
                        .font(.callout)
                        .foregroundStyle(Color.screen.opacity(0.7))
                        .lineLimit(3)
                        .lineSpacing(2)
                        .multilineTextAlignment(.leading)
                        .padding(.top, 4)
                }
            }
            .frame(maxWidth: 720, alignment: .leading)
            Spacer(minLength: 0)
        }
        .contentShape(Rectangle())
    }

    private func thumbnail(width: CGFloat) -> some View {
        Color.seat
            .frame(width: width, height: width * 9 / 16)
            .overlay {
                RemoteImage(url: model.client?.imageURL(episode.artwork.thumb ?? episode.artwork.backdrop, width: width > 200 ? 640 : 400)) {
                    Image(systemName: Symbol.play).foregroundStyle(Color.usher)
                }
            }
            .overlay(alignment: .bottom) {
                if let progress = episode.progress { ProgressBar(value: progress).padding(6) }
            }
            .overlay(alignment: .topTrailing) {
                if episode.userData.watched { WatchedBadge().padding(5) }
            }
            .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
    }

    /// A phone: the picture and title side by side, the overview underneath.
    private var narrow: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 12) {
                thumbnail(width: 140)
                VStack(alignment: .leading, spacing: 3) {
                    Text(episode.episodeNumber.map { t("Episode {number}", ["number": $0]) } ?? t("Special"))
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(isCurrent ? Color.screen : Color.usher)
                    Text(episode.title)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Color.screen)
                        .lineLimit(2)
                    if let time = timeText {
                        Text(time).font(.caption).foregroundStyle(Color.usher)
                    }
                }
                Spacer(minLength: 0)
            }
            if let overview, !overview.isEmpty {
                Text(overview)
                    .font(.footnote)
                    .foregroundStyle(Color.usher)
                    .lineLimit(3)
                    .multilineTextAlignment(.leading)
            }
        }
        .contentShape(Rectangle())
    }

    private var timeText: String? {
        if episode.progress != nil, let duration = episode.effectiveDuration {
            return Format.remaining(duration - episode.userData.positionSeconds)
        }
        return episode.runtimeMinutes.flatMap(Format.runtime(minutes:)) ?? episode.durationSeconds.flatMap(Format.runtime(seconds:))
    }
}
