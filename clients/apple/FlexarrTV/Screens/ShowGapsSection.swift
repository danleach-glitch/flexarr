import FlexarrKit
import SwiftUI

/// A show's *Not in your library* (seasons it has none of, and the missing episodes of seasons it has some of) and
/// *Coming up* (the next episodes Sonarr has dates for). Requests go to Seerr for just that season; Search asks Sonarr
/// (the main profile). Pressing a season requested through Seerr asks (Keep first), then takes back just that season.
struct ShowGapsSection: View {
    @Environment(AppModel.self) private var model
    let showId: String
    /// The show's TMDB id, for requesting a season through Seerr.
    let tmdbId: Int?
    let gaps: ShowGaps
    /// Seasons asked for or searched here, until the page next loads.
    @State private var asked: Set<Int> = []
    /// Whole seasons Sonarr was asked to search for here: those aren't Seerr's to take back.
    @State private var searched: Set<Int> = []
    /// Seasons whose request was taken back here, until the page next loads.
    @State private var undone: Set<Int> = []
    /// The season whose Requested was pressed: asked about before it's taken back.
    @State private var undoing: MissingSeason?
    @State private var busy = false

    var body: some View {
        VStack(alignment: .leading, spacing: 40) {
            if gaps.hasMissing {
                VStack(alignment: .leading, spacing: 0) {
                    RowHeading(title: "Not in your library")
                    if !gaps.seasons.isEmpty {
                        ScrollView(.horizontal) {
                            LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                                ForEach(gaps.seasons) { season in
                                    seasonCard(season)
                                }
                            }
                            .padding(.horizontal, Layout.edge)
                            .padding(.top, 28)
                            .padding(.bottom, 40)
                        }
                        .scrollClipDisabled()
                        .focusSection()
                    }
                    VStack(alignment: .leading, spacing: 24) {
                        ForEach(gaps.episodesBySeason, id: \.season) { group in
                            missingEpisodes(season: group.season, episodes: group.episodes)
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                    .padding(.top, gaps.seasons.isEmpty ? 24 : 0)
                }
            }
            if !gaps.upcoming.isEmpty {
                VStack(alignment: .leading, spacing: 16) {
                    RowHeading(title: "Coming up")
                    VStack(alignment: .leading, spacing: 10) {
                        ForEach(gaps.upcoming) { episode in
                            HStack(alignment: .firstTextBaseline, spacing: 18) {
                                Text(episode.code)
                                    .font(.system(size: 26, weight: .semibold).monospacedDigit())
                                    .foregroundStyle(Color.screen)
                                Text(episode.title ?? "")
                                    .font(.system(size: 26))
                                    .foregroundStyle(Color.screen.opacity(0.85))
                                    .lineLimit(1)
                                if let airs = episode.airs() {
                                    Text(airs).font(.system(size: 26)).foregroundStyle(Color.usher)
                                }
                            }
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                }
            }
        }
        .undoConfirmation(TitleWords.unrequest(undoing?.title ?? ""), isPresented: Binding(get: { undoing != nil }, set: { if !$0 { undoing = nil } }),
                          keep: TitleWords.keepIt, undo: TitleWords.unrequestAction) {
            if let season = undoing { Task { await unrequest(season) } }
        }
    }

    private func state(_ season: MissingSeason) -> MissingSeasonAction {
        let n = season.seasonNumber
        if asked.contains(n) || searched.contains(n) { return .requested }
        if undone.contains(n), gaps.canRequest { return .request }
        return gaps.action(for: season)
    }

    /// A season requested through Seerr can be taken back from here; one Sonarr is searching for can't.
    private func undoable(_ season: MissingSeason) -> Bool {
        gaps.canRequest && tmdbId != nil && !searched.contains(season.seasonNumber) && season.availability != .partlyAvailable
    }

    private func seasonCard(_ season: MissingSeason) -> some View {
        let action = state(season)
        let width = Layout.posterWidth
        // Always a button, whatever it can do: one that's disabled while it has focus leaves nothing focused.
        return ArtworkButton(ambient: .silver, accessibilityText: season.title) {
            // Pressing it does what it says, as on Android TV: no dialog in between.
            guard !busy else { return }
            switch action {
            case .request: Task { await request(season) }
            case .search: Task { await search(season.seasonNumber) }
            case .requested where undoable(season): undoing = season
            default: break
            }
        } artwork: {
            RemoteImage(url: model.client?.imageURL(season.poster, width: 342)) {
                ZStack {
                    LinearGradient(colors: [Color.silver.opacity(0.3), Color.seat], startPoint: .top, endPoint: .bottom)
                    Text(season.title)
                        .font(.marquee(30, weight: .bold))
                        .foregroundStyle(Color.screen.opacity(0.8))
                        .multilineTextAlignment(.center)
                        .padding(16)
                }
            }
            .frame(width: width, height: width * 1.5)
            .opacity(action == .requested ? 0.55 : 1)
            .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(season.title)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.8))
                Text(caption(season))
                    .font(.system(size: 21))
                    .foregroundStyle(Color.usher)
                if let status = status(action) {
                    Label(status.title, systemImage: status.symbol)
                        .font(.system(size: 21, weight: .semibold))
                        .foregroundStyle(Color.screen)
                }
            }
            .lineLimit(1)
            .frame(width: width, alignment: .leading)
        }
    }

    private func caption(_ season: MissingSeason) -> String {
        [season.episodes > 0 ? (season.episodes == 1 ? "1 episode" : "\(season.episodes) episodes") : nil,
         season.year.map(String.init)].compactMap(\.self).joined(separator: ", ")
    }

    /// What pressing the card does, or that it's done: Request, Search or Requested.
    private func status(_ action: MissingSeasonAction) -> (title: String, symbol: String)? {
        switch action {
        case .request: ("Request", Symbol.request)
        case .search: ("Search", Symbol.search)
        case .requested: ("Requested", Symbol.requested)
        default: nil
        }
    }

    private func missingEpisodes(season: Int, episodes: [ShowEpisode]) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Season \(season): \(episodes.count == 1 ? "1 episode" : "\(episodes.count) episodes")")
                .font(.system(size: 30, weight: .bold))
                .foregroundStyle(Color.screen)
            Text(episodes.map { [$0.code, $0.title].compactMap(\.self).joined(separator: " ") }.joined(separator: "   "))
                .font(.system(size: 24))
                .foregroundStyle(Color.screen.opacity(0.8))
                .lineLimit(2)
                .frame(maxWidth: 1400, alignment: .leading)
            if gaps.canSearch && gaps.source == "sonarr" {
                Button {
                    guard !busy, !asked.contains(-season) else { return }
                    Task { await search(season, episodesOnly: true) }
                } label: {
                    Label(asked.contains(-season) ? "Searching" : "Search", systemImage: Symbol.search)
                }
                .buttonStyle(ActionButtonStyle())
                .accessibilityLabel("Search for season \(season)'s missing episodes")
                .padding(.top, 8)
            }
        }
        .focusSection()
    }

    private func request(_ season: MissingSeason) async {
        guard let client = model.client, let tmdbId else { return }
        busy = true
        defer { busy = false }
        do {
            try await client.requestSeasons(tmdbId: tmdbId, seasons: [season.seasonNumber])
            asked.insert(season.seasonNumber)
            undone.remove(season.seasonNumber)
        } catch {
            model.requestError = ConnectionError(error).message
        }
    }

    /// Takes back just this season's request, for the household.
    private func unrequest(_ season: MissingSeason) async {
        guard let tmdbId else { return }
        busy = true
        defer { busy = false }
        if let failure = await model.unrequestTitle(mediaType: "tv", tmdbId: tmdbId, seasons: [season.seasonNumber]) {
            model.requestError = failure
        } else {
            asked.remove(season.seasonNumber)
            undone.insert(season.seasonNumber)
        }
    }

    /// `episodesOnly`: a season the library has some of (remembered as its negative, beside whole seasons).
    private func search(_ season: Int, episodesOnly: Bool = false) async {
        guard let client = model.client else { return }
        busy = true
        defer { busy = false }
        do {
            try await client.searchMissing(showId: showId, season: season)
            if episodesOnly { asked.insert(-season) } else { searched.insert(season) }
        } catch {
            model.requestError = ConnectionError(error).message
        }
    }
}
