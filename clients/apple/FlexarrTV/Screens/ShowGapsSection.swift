import FlexarrKit
import SwiftUI

/// A show's *Not in your library* (seasons it has none of, and how many episodes are missing from seasons it has some of) and
/// *Coming up* (the next episodes Sonarr has dates for). Requests go to Seerr for just that season; Search asks Sonarr
/// (the main profile). Pressing a season requested through Seerr asks (Keep first), then takes back just that season.
struct ShowGapsSection: View {
    @Environment(AppModel.self) private var model
    let showId: String
    /// The show's TMDB id, for requesting a season through Seerr.
    let tmdbId: Int?
    let gaps: ShowGaps
    /// The library's own seasons' posters, by season number, for a season it has some of (else the show's poster).
    var posters: [Int: String] = [:]
    var showPoster: String?
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
                    RowHeading(title: t(Words.notInYourLibrary))
                    // One row: a season it has some of is a card beside those it has none of, saying how many are missing.
                    ScrollView(.horizontal) {
                        LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                            ForEach(gaps.row(posters: posters, showPoster: showPoster), id: \.season.seasonNumber) { entry in
                                seasonCard(entry.season, partial: entry.partial)
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
            if !gaps.upcoming.isEmpty {
                VStack(alignment: .leading, spacing: 16) {
                    RowHeading(title: t("Coming up"))
                    VStack(alignment: .leading, spacing: 6) {
                        ForEach(gaps.upcoming) { episode in
                            listRow {
                                HStack(alignment: .firstTextBaseline, spacing: 18) {
                                    Text(episode.code)
                                        .font(.system(size: 26, weight: .semibold).monospacedDigit())
                                    Text(episode.title ?? "")
                                        .font(.system(size: 26))
                                        .lineLimit(1)
                                    if let airs = episode.airs() {
                                        Text(airs).font(.system(size: 26))
                                    }
                                }
                            }
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                    .focusSection()
                }
            }
        }
        .undoConfirmation(TitleWords.unrequest(undoing?.title ?? ""), isPresented: Binding(get: { undoing != nil }, set: { if !$0 { undoing = nil } }),
                          keep: TitleWords.keepIt, undo: TitleWords.unrequestAction) {
            if let season = undoing { Task { await unrequest(season) } }
        }
    }

    private func state(_ season: MissingSeason, partial: Bool) -> MissingSeasonAction {
        let n = season.seasonNumber
        // A season it has some of: Search for the rest (the main profile), Searching once asked, or nothing.
        if partial { return asked.contains(-n) ? .requested : gaps.canSearch && gaps.source == "sonarr" ? .search : .none }
        if asked.contains(n) || searched.contains(n) { return .requested }
        if undone.contains(n), gaps.canRequest { return .request }
        let action = gaps.action(for: season)
        // Without the show's TMDB id there's nothing to request it by: Search (the main profile) or nothing.
        if action == .request, tmdbId == nil { return gaps.canSearch && gaps.source == "sonarr" ? .search : .none }
        return action
    }

    /// A season requested through Seerr can be taken back from here; one Sonarr is searching for can't.
    private func undoable(_ season: MissingSeason) -> Bool {
        gaps.canRequest && tmdbId != nil && !searched.contains(season.seasonNumber) && season.availability != .partlyAvailable
    }

    private func seasonCard(_ season: MissingSeason, partial: Bool) -> some View {
        let action = state(season, partial: partial)
        let width = Layout.posterWidth
        let badge = partial && action == .requested ? (title: t("Searching"), symbol: Symbol.search) : status(action)
        let caption = caption(season, partial: partial)
        // Always a button, whatever it can do: one that's disabled while it has focus leaves nothing focused.
        // VoiceOver hears what the caption says, Request, Search or Requested among it, as the card is all it focuses.
        return ArtworkButton(ambient: .silver,
                             accessibilityText: [season.title, caption, badge?.title].compactMap(\.self)
                                 .filter { !$0.isEmpty }.joined(separator: ", "),
                             accessibilityHint: !partial && action == .requested && undoable(season) ? TitleWords.unrequestHint : nil,
                             isSelected: action == .requested) {
            // Pressing it does what it says, as on Android TV: no dialog in between.
            guard !busy else { return }
            switch action {
            case .request: Task { await request(season) }
            case .search: Task { await search(season.seasonNumber, episodesOnly: partial) }
            case .requested where !partial && undoable(season): undoing = season
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
            .opacity(action == .requested && !partial ? 0.55 : 1)
            .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(season.title)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.8))
                Text(caption)
                    .font(.system(size: 21))
                    .foregroundStyle(Color.usher)
                if let status = badge {
                    Label(status.title, systemImage: status.symbol)
                        .font(.system(size: 21, weight: .semibold))
                        .foregroundStyle(Color.screen)
                }
            }
            .lineLimit(1)
            .frame(width: width, alignment: .leading)
            .accessibilityHidden(true)
        }
    }

    private func caption(_ season: MissingSeason, partial: Bool) -> String {
        if partial { return t("{count, plural, one {# episode missing} other {# episodes missing}}", ["count": season.episodes]) }
        return [season.episodes > 0 ? t("{count, plural, one {# episode} other {# episodes}}", ["count": season.episodes]) : nil,
         season.year.map(String.init)].compactMap(\.self).joined(separator: ", ")
    }

    /// What pressing the card does, or that it's done: Request, Search or Requested.
    private func status(_ action: MissingSeasonAction) -> (title: String, symbol: String)? {
        switch action {
        case .request: (TitleWords.request, Symbol.request)
        case .search: (t(Words.search), Symbol.search)
        case .requested: (TitleWords.requested, Symbol.requested)
        default: nil
        }
    }

    /// A focusable row that does nothing, as an outside album's tracks are: the remote scrolls only by moving focus, so
    /// without it the lines are skipped on the way down (for everyone but the main profile, nothing else here takes
    /// focus). Its highlight reaches out into the margin, the words staying in line with the headings.
    private func listRow<Content: View>(@ViewBuilder _ label: () -> Content) -> some View {
        Button {} label: { label() }
            .buttonStyle(TrackRowStyle(isCurrent: false))
            .padding(.horizontal, -30)
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
