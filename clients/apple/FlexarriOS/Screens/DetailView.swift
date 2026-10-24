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
    @Environment(\.scenePhase) private var scenePhase

    private var item: ItemSummary? { detail?.item ?? (preview?.kind == .season ? nil : preview) }
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
            if let item {
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
                    FactsLine(facts: item.facts)
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
                    .accessibilityHint(overviewExpanded ? "Shows less" : "Shows the whole overview")
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

    @ViewBuilder
    private func actions(_ item: ItemSummary) -> some View {
        let playable = playTarget(item)
        let watched = item.userData.watched || item.userData.unwatchedCount == 0
        HStack(spacing: 10) {
            if let playable {
                Button { model.play(playable) } label: {
                    Label(playLabel(playable, container: item), systemImage: "play.fill")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(ActionButtonStyle(ambient: ambient, prominent: true))
                .monospacedDigit()

                if playable.isResumable {
                    Button { model.play(playable, fromBeginning: true) } label: {
                        Label("Start over", systemImage: "arrow.counterclockwise")
                    }
                    .buttonStyle(RoundActionStyle())
                }
            }

            if item.kind == .series, item.childCount > 0 {
                Button { model.shuffle(item) } label: {
                    Label("Shuffle", systemImage: "shuffle")
                }
                .buttonStyle(RoundActionStyle())
            }

            Button { Task { await model.setWatched(item, !watched) } } label: {
                Label(watched ? "Mark as unwatched" : "Mark as watched", systemImage: watched ? "eye.slash" : "checkmark")
            }
            .buttonStyle(RoundActionStyle())

            if item.kind == .episode, let series {
                Button { router.push(.item(series)) } label: {
                    Label("Go to show", systemImage: "rectangle.stack")
                }
                .buttonStyle(RoundActionStyle())
            }
        }
        .frame(maxWidth: 520, alignment: .leading)
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
                VStack(alignment: .leading, spacing: 10) {
                    SectionHeading(title: season.item.title)
                    episodesList(season)
                }
            }

            let cast = detail.credits.sorted { rank($0.role) < rank($1.role) }
            if !cast.isEmpty {
                VStack(alignment: .leading, spacing: 10) {
                    SectionHeading(title: "Cast and crew")
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
                    SectionHeading(title: "More like this")
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
                ScrollViewReader { proxy in
                    ScrollView(.horizontal) {
                        HStack(spacing: 8) {
                            ForEach(seasonList) { season in
                                Button(season.title) { selectedSeasonID = season.id }
                                    .buttonStyle(PillButtonStyle(selected: season.id == selectedSeasonID, ambient: ambient))
                                    .id(season.id)
                            }
                        }
                        .padding(.horizontal, Layout.edge)
                    }
                    .scrollIndicators(.hidden)
                    .onAppear { if let selectedSeasonID { proxy.scrollTo(selectedSeasonID, anchor: .center) } }
                }
            } else if let only = seasonList.first {
                SectionHeading(title: only.title)
            }

            if let season = selectedSeason {
                episodesList(season)
            } else {
                ProgressView().frame(maxWidth: .infinity).frame(height: 200)
            }
        }
        .task(id: selectedSeasonID) { await loadSelectedSeason() }
    }

    private func episodesList(_ season: ItemDetail) -> some View {
        // Two columns on an iPad, which would otherwise leave half the page empty.
        let columns = Array(repeating: GridItem(.flexible(), spacing: 24, alignment: .top), count: sizeClass == .regular ? 2 : 1)
        return LazyVGrid(columns: columns, alignment: .leading, spacing: 16) {
            ForEach(season.children) { episode in
                EpisodeRow(episode: episode, overview: season.childOverviews[episode.id] ?? nil,
                           isCurrent: episode.id == item?.id) {
                    if episode.id == item?.id || item?.kind == .series {
                        model.play(episode)
                    } else {
                        router.push(.item(episode))
                    }
                }
            }
        }
        .padding(.horizontal, Layout.edge)
    }

    private func fileInfo(_ file: MediaFile) -> some View {
        let video = [file.height.map { "\($0)p" }, file.videoCodec?.uppercased(), file.videoRange == .sdr ? nil : file.videoRange.label]
            .compactMap { $0 }.joined(separator: " ")
        let audio = [file.audioCodec?.uppercased(), file.audioChannels.map(AudioChannels.label)].compactMap { $0 }.joined(separator: " ")
        return LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), alignment: .topLeading)], alignment: .leading, spacing: 14) {
            if !video.isEmpty { labeled("Video", video) }
            if !audio.isEmpty { labeled("Audio", audio) }
            if let studio = detail?.studio { labeled("Studio", studio) }
            labeled("Size", ByteCountFormatter.string(fromByteCount: file.size, countStyle: .file))
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

/// An episode in a season list: still with progress beside the number, title and time, then the overview.
struct EpisodeRow: View {
    @Environment(AppModel.self) private var model
    let episode: ItemSummary
    let overview: String?
    var isCurrent = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 12) {
                    Color.seat
                        .frame(width: 140, height: 140 * 9 / 16)
                        .overlay {
                            RemoteImage(url: model.client?.imageURL(episode.artwork.thumb ?? episode.artwork.backdrop, width: 400)) {
                                Image(systemName: "play.fill").foregroundStyle(Color.usher)
                            }
                        }
                        .overlay(alignment: .bottom) {
                            if let progress = episode.progress { ProgressBar(value: progress).padding(6) }
                        }
                        .overlay(alignment: .topTrailing) {
                            if episode.userData.watched { WatchedBadge().padding(5) }
                        }
                        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                    VStack(alignment: .leading, spacing: 3) {
                        Text(episode.episodeNumber.map { "Episode \($0)" } ?? "Special")
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
        .buttonStyle(CardButtonStyle())
        .contextMenu { ItemContextMenu(item: episode, showsDetails: true) }
    }

    private var timeText: String? {
        if episode.progress != nil, let duration = episode.effectiveDuration {
            return Format.remaining(duration - episode.userData.positionSeconds)
        }
        return episode.runtimeMinutes.flatMap(Format.runtime(minutes:)) ?? episode.durationSeconds.flatMap(Format.runtime(seconds:))
    }
}
