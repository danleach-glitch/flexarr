import FlexarrKit
import SwiftUI

/// A show's *Not in your library* (seasons it has none of, and how many episodes are missing from seasons it has some of) and
/// *Coming up* (the next episodes Sonarr has dates for). Requests go to Seerr for just that season; Search asks Sonarr
/// (the main profile).
struct ShowGapsSection: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    let showId: String
    /// The show's TMDB id, for requesting a season through Seerr.
    let tmdbId: Int?
    @Binding var gaps: ShowGaps
    /// The library's own seasons' posters, by season number, for a season it has some of (else the show's poster).
    var posters: [Int: String] = [:]
    var showPoster: String?
    /// Seasons asked for here, until the page next loads.
    @State private var asked: Set<Int> = []
    /// Seasons whose request was taken back here, until the page next loads.
    @State private var undone: Set<Int> = []
    /// The season whose Requested was pressed: asked about before it's taken back.
    @State private var undoing: MissingSeason?
    @State private var searched: Set<Int> = []
    /// Seasons it has some of whose missing episodes Sonarr was asked to search for here.
    @State private var searchedEpisodes: Set<Int> = []
    @State private var busy = false
    @State private var said: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 28) {
            if gaps.hasMissing {
                VStack(alignment: .leading, spacing: 12) {
                    SectionHeading(title: t(Words.notInYourLibrary))
                    // One row: a season it has some of is a card beside those it has none of, saying how many are missing.
                    ScrollView(.horizontal) {
                        LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                            ForEach(gaps.row(posters: posters, showPoster: showPoster), id: \.season.seasonNumber) { entry in
                                seasonCard(entry.season, partial: entry.partial)
                            }
                        }
                        .padding(.horizontal, Layout.edge)
                    }
                    .scrollIndicators(.hidden)
                }
            }
            if !gaps.upcoming.isEmpty {
                VStack(alignment: .leading, spacing: 10) {
                    SectionHeading(title: t("Coming up"))
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(gaps.upcoming) { episode in
                            episodeLine(episode, detail: episode.airs())
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                }
            }
        }
        .banner($said)
        .confirmationDialog(TitleWords.unrequest(undoing?.title ?? ""), isPresented: Binding(get: { undoing != nil }, set: { if !$0 { undoing = nil } }),
                            titleVisibility: .visible, presenting: undoing) { season in
            Button(TitleWords.unrequestAction, role: .destructive) { Task { await unrequest(season) } }
            Button(TitleWords.keepIt, role: .cancel) {}
        }
    }

    private func state(_ season: MissingSeason, partial: Bool) -> MissingSeasonAction {
        let n = season.seasonNumber
        // A season it has some of: Search for the rest (the main profile), Searching once asked, or nothing.
        if partial { return searchedEpisodes.contains(n) ? .requested : gaps.canSearch && gaps.source == "sonarr" ? .search : .none }
        if asked.contains(n) || searched.contains(n) { return .requested }
        if undone.contains(n), gaps.canRequest { return .request }
        let action = gaps.action(for: season)
        // Without the show's TMDB id there's nothing to request it by: Search (the main profile) or nothing.
        if action == .request, tmdbId == nil { return gaps.canSearch && gaps.source == "sonarr" ? .search : .none }
        return action
    }

    /// A season requested through Seerr can be taken back from here, as on the web; one Sonarr is searching for can't.
    private func undoable(_ season: MissingSeason) -> Bool {
        gaps.canRequest && tmdbId != nil && !searched.contains(season.seasonNumber) && season.availability != .partlyAvailable
    }

    /// Poster, name and episodes, with Request (or Search) under it, as on the web.
    private func seasonCard(_ season: MissingSeason, partial: Bool) -> some View {
        let width = Layout.posterWidth(sizeClass)
        let action = state(season, partial: partial)
        return VStack(alignment: .leading, spacing: 6) {
            Color.seat
                .aspectRatio(2 / 3, contentMode: .fit)
                .overlay {
                    RemoteImage(url: model.client?.imageURL(season.poster, width: 342)) {
                        Text(season.title)
                            .font(.marquee(18, weight: .bold, scales: false))
                            .foregroundStyle(Color.screen.opacity(0.8))
                            .multilineTextAlignment(.center)
                            .padding(8)
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 1) {
                Text(season.title)
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Color.screen.opacity(0.9))
                Text(caption(season, partial: partial))
                    .font(.caption)
                    .foregroundStyle(Color.usher)
            }
            .lineLimit(1)
            .accessibilityElement(children: .combine)
            switch action {
            case .request:
                actionButton(TitleWords.request, symbol: Symbol.request, label: t("Request {season}", ["season": season.title])) { await request(season) }
            case .search where partial:
                actionButton(t(Words.search), symbol: Symbol.search,
                             label: t("Search for season {season}’s missing episodes", ["season": season.seasonNumber])) {
                    await search(season.seasonNumber, episodesOnly: true)
                }
            case .search:
                actionButton(t(Words.search), symbol: Symbol.search, label: t("Search for {season}", ["season": season.title])) { await search(season.seasonNumber) }
            case .requested where !partial && undoable(season):
                Button { undoing = season } label: { Label(TitleWords.requested, systemImage: Symbol.requested).oneLine() }
                    .buttonStyle(.borderedProminent)
                    .tint(Color.seat)
                    .foregroundStyle(Color.screen)
                    .controlSize(.small)
                    .disabled(busy)
                    .accessibilityLabel(t("Requested, {season}", ["season": season.title]))
                    .accessibilityHint(TitleWords.unrequestHint)
                    .accessibilityAddTraits(.isSelected)
            case .requested:
                Text(partial || searched.contains(season.seasonNumber) ? t("Searching") : TitleWords.requested)
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Color.usher)
            default:
                EmptyView()
            }
        }
        .frame(width: width, alignment: .leading)
    }

    private func actionButton(_ title: String, symbol: String, label: String, run: @escaping () async -> Void) -> some View {
        Button { Task { await run() } } label: { Label(title, systemImage: symbol).oneLine() }
            .buttonStyle(.bordered)
            .tint(Color.screen)
            .controlSize(.small)
            .disabled(busy)
            .accessibilityLabel(label)
    }

    private func caption(_ season: MissingSeason, partial: Bool) -> String {
        if partial { return t("{count, plural, one {# episode missing} other {# episodes missing}}", ["count": season.episodes]) }
        return [season.episodes > 0 ? t("{count, plural, one {# episode} other {# episodes}}", ["count": season.episodes]) : nil,
         season.year.map(String.init)].compactMap(\.self).joined(separator: ", ")
    }

    /// "S2E3  Title  12 Nov": the code, the title (or "Episode 3") and a date, as on the web.
    private func episodeLine(_ episode: ShowEpisode, detail: String?) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(episode.code)
                .font(.subheadline.weight(.semibold).monospacedDigit())
                .foregroundStyle(Color.usher)
            Text(episode.title ?? t("Episode {number}", ["number": episode.episodeNumber]))
                .font(.subheadline)
                .foregroundStyle(Color.screen.opacity(0.85))
                .lineLimit(1)
            Spacer(minLength: 8)
            if let detail {
                Text(detail).font(.subheadline).foregroundStyle(Color.usher)
            }
        }
        .accessibilityElement(children: .combine)
    }

    private func request(_ season: MissingSeason) async {
        guard let client = model.client, let tmdbId else { return }
        busy = true
        defer { busy = false }
        do {
            try await client.requestSeasons(tmdbId: tmdbId, seasons: [season.seasonNumber])
            asked.insert(season.seasonNumber)
            undone.remove(season.seasonNumber)
            said = t("Requested, {season}", ["season": season.title])
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

    private func search(_ season: Int, episodesOnly: Bool = false) async {
        guard let client = model.client else { return }
        busy = true
        defer { busy = false }
        do {
            try await client.searchMissing(showId: showId, season: season)
            if episodesOnly { searchedEpisodes.insert(season) } else { searched.insert(season) }
            said = t("Sonarr is searching")
        } catch {
            model.requestError = ConnectionError(error).message
        }
    }
}

private extension View {
    /// Keeps a card's button on one line in a poster's width ("Requested" broke as "Reques-ted"): smaller before it wraps.
    func oneLine() -> some View {
        lineLimit(1).minimumScaleFactor(0.7)
    }
}
