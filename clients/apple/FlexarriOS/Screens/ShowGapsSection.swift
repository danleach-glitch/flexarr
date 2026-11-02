import FlexarrKit
import SwiftUI

/// A show's *Not in your library* (seasons it has none of, and the missing episodes of seasons it has some of) and
/// *Coming up* (the next episodes Sonarr has dates for). Requests go to Seerr for just that season; Search asks Sonarr
/// (the main profile).
struct ShowGapsSection: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    let showId: String
    /// The show's TMDB id, for requesting a season through Seerr.
    let tmdbId: Int?
    @Binding var gaps: ShowGaps
    /// Seasons asked for here, until the page next loads.
    @State private var asked: Set<Int> = []
    @State private var searched: Set<Int> = []
    @State private var busy = false
    @State private var choosing: MissingSeason?
    @State private var said: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 28) {
            if gaps.hasMissing {
                VStack(alignment: .leading, spacing: 12) {
                    SectionHeading(title: "Not in your library")
                    if !gaps.seasons.isEmpty {
                        ScrollView(.horizontal) {
                            LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                                ForEach(gaps.seasons) { season in
                                    seasonCard(season)
                                }
                            }
                            .padding(.horizontal, Layout.edge)
                        }
                        .scrollIndicators(.hidden)
                    }
                    ForEach(gaps.episodesBySeason, id: \.season) { group in
                        missingEpisodes(season: group.season, episodes: group.episodes)
                    }
                }
            }
            if !gaps.upcoming.isEmpty {
                VStack(alignment: .leading, spacing: 10) {
                    SectionHeading(title: "Coming up")
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(gaps.upcoming) { episode in
                            HStack(alignment: .firstTextBaseline, spacing: 10) {
                                Text(episode.code)
                                    .font(.subheadline.weight(.semibold).monospacedDigit())
                                    .foregroundStyle(Color.screen)
                                Text(episode.title ?? "")
                                    .font(.subheadline)
                                    .foregroundStyle(Color.screen.opacity(0.85))
                                    .lineLimit(1)
                                Spacer(minLength: 8)
                                if let airs = episode.airs() {
                                    Text(airs).font(.subheadline).foregroundStyle(Color.usher)
                                }
                            }
                            .accessibilityElement(children: .combine)
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                }
            }
        }
        .confirmationDialog(choosing?.title ?? "", isPresented: Binding(get: { choosing != nil }, set: { if !$0 { choosing = nil } }),
                            titleVisibility: .visible, presenting: choosing) { season in
            switch gaps.action(for: season) {
            case .request:
                Button { Task { await request(season) } } label: { Label("Request", systemImage: Symbol.request) }
            case .search:
                Button { Task { await search(season.seasonNumber) } } label: { Label("Search", systemImage: Symbol.search) }
            default:
                EmptyView()
            }
            Button("Cancel", role: .cancel) {}
        }
        .banner($said)
    }

    private func state(_ season: MissingSeason) -> MissingSeasonAction {
        asked.contains(season.seasonNumber) ? .requested : searched.contains(season.seasonNumber) ? .requested : gaps.action(for: season)
    }

    private func seasonCard(_ season: MissingSeason) -> some View {
        let width = Layout.posterWidth(sizeClass)
        let action = state(season)
        return Button {
            if action == .request || action == .search { choosing = season }
        } label: {
            VStack(alignment: .leading, spacing: 6) {
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
                    .opacity(action == .requested ? 0.55 : 1)
                    .overlay(alignment: .bottomTrailing) {
                        if action == .request || action == .search {
                            Image(systemName: action == .request ? Symbol.request : Symbol.search)
                                .font(.caption.weight(.bold))
                                .foregroundStyle(Color.screen)
                                .padding(7)
                                .background(Circle().fill(.black.opacity(0.55)))
                                .padding(6)
                        }
                    }
                VStack(alignment: .leading, spacing: 1) {
                    Text(season.title)
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                    Text(caption(season, action))
                        .font(.caption)
                        .foregroundStyle(Color.usher)
                }
                .lineLimit(1)
            }
            .frame(width: width)
            .contentShape(Rectangle())
        }
        .buttonStyle(CardButtonStyle())
        .disabled(busy || !(action == .request || action == .search))
        .accessibilityElement(children: .combine)
        .accessibilityHint(action == .request ? "Requests it" : action == .search ? "Asks Sonarr to search for it" : "")
    }

    private func caption(_ season: MissingSeason, _ action: MissingSeasonAction) -> String {
        [season.episodes > 0 ? (season.episodes == 1 ? "1 episode" : "\(season.episodes) episodes") : nil,
         season.year.map(String.init),
         action == .requested ? "Requested" : nil].compactMap(\.self).joined(separator: ", ")
    }

    private func missingEpisodes(season: Int, episodes: [ShowEpisode]) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text("Season \(season)")
                    .font(.headline)
                    .foregroundStyle(Color.screen)
                Text(episodes.count == 1 ? "1 episode missing" : "\(episodes.count) episodes missing")
                    .font(.subheadline)
                    .foregroundStyle(Color.usher)
                Spacer()
                if gaps.canSearch && gaps.source == "sonarr" {
                    if searched.contains(season) {
                        Text("Searching").font(.subheadline.weight(.semibold)).foregroundStyle(Color.usher)
                    } else {
                        Button { Task { await search(season) } } label: { Label("Search", systemImage: Symbol.search) }
                            .buttonStyle(.bordered)
                            .tint(Color.screen)
                            .controlSize(.small)
                            .disabled(busy)
                            .accessibilityLabel("Search for season \(season)'s missing episodes")
                    }
                }
            }
            ForEach(episodes) { episode in
                Text([episode.code, episode.title].compactMap(\.self).joined(separator: " · "))
                    .font(.subheadline)
                    .foregroundStyle(Color.screen.opacity(0.8))
                    .lineLimit(1)
            }
        }
        .padding(.horizontal, Layout.edge)
    }

    private func request(_ season: MissingSeason) async {
        guard let client = model.client, let tmdbId else { return }
        busy = true
        defer { busy = false }
        do {
            try await client.requestSeasons(tmdbId: tmdbId, seasons: [season.seasonNumber])
            asked.insert(season.seasonNumber)
            said = "Requested \(season.title)"
        } catch {
            model.requestError = ConnectionError(error).message
        }
    }

    private func search(_ season: Int) async {
        guard let client = model.client else { return }
        busy = true
        defer { busy = false }
        do {
            try await client.searchMissing(showId: showId, season: season)
            searched.insert(season)
            said = "Sonarr is searching"
        } catch {
            model.requestError = ConnectionError(error).message
        }
    }
}
