import FlexarrKit
import SwiftUI

/// A film or show that isn't in the library, from search: what it is, how it's rated, who's in it, its trailer, and
/// Request and Watchlist. One that's come in since opens as itself.
struct ExternalTitleView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.verticalSizeClass) private var verticalSizeClass
    let preview: ExternalTitle
    @State private var detail: ExternalDetail?
    @State private var error: String?
    @State private var requesting = false
    /// How the last request here turned out, when it didn't go through: said under the button.
    @State private var outcome: TitleRequestOutcome?
    @State private var confirmingUndo = false
    /// Seerr isn't answering: Request and Requested wait, greyed.
    @State private var seerrPaused = false
    @State private var onWatchlist: Bool?
    @State private var showingTrailer = false

    private var title: ExternalTitle { detail?.summary ?? preview }
    private var heroHeight: CGFloat { verticalSizeClass == .compact ? 280 : sizeClass == .regular ? 520 : 420 }

    var body: some View {
        ZStack {
            RoomBackground(ambient: .silver, intensity: 0.22)
            if let id = detail?.libraryItemId {
                DetailView(id: id, preview: nil)
            } else {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 28) {
                        header
                        if let detail {
                            sections(detail)
                        } else if let error {
                            StatusView(state: .failed(error) { Task { await load() } }).frame(height: 240)
                        }
                    }
                    .padding(.bottom, 40)
                }
                .ignoresSafeArea(edges: .top)
                .refreshable { await load() }
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(.hidden, for: .navigationBar)
        .sheet(isPresented: $showingTrailer) {
            if let trailer { TrailerSheet(trailer: trailer, title: title.title) }
        }
        .task { await load() }
    }

    private var trailer: YouTubeTrailer? { YouTubeTrailer(id: detail?.trailerYouTubeId) }

    private var header: some View {
        VStack(alignment: .leading, spacing: 14) {
            ZStack(alignment: .bottomLeading) {
                Color.clear
                    .frame(height: heroHeight)
                    .frame(maxWidth: .infinity)
                    .overlay {
                        RemoteImage(url: model.client?.imageURL(title.backdrop, width: sizeClass == .regular ? 1600 : 1000)) { Color.room }
                    }
                    .clipped()
                    .overlay {
                        LinearGradient(stops: [.init(color: .clear, location: 0.3), .init(color: .room.opacity(0.6), location: 1)], startPoint: .top, endPoint: .bottom)
                    }
                    .mask(LinearGradient(stops: [.init(color: .black, location: 0.55), .init(color: .clear, location: 1)], startPoint: .top, endPoint: .bottom))
                VStack(alignment: .leading, spacing: 8) {
                    Text(title.title)
                        .font(.marquee(title.title.count > 24 ? 34 : 44, weight: .heavy))
                        .foregroundStyle(Color.screen)
                        .lineLimit(3)
                        .minimumScaleFactor(0.7)
                    FactsLine(facts: facts, scores: model.isKids ? nil : detail?.scores)
                    if let genres = detail?.genres, !genres.isEmpty {
                        Text(genres.prefix(3).joined(separator: ", "))
                            .font(.subheadline.weight(.medium))
                            .foregroundStyle(Color.usher)
                    }
                }
                .padding(.horizontal, Layout.edge)
            }

            actions
                .padding(.horizontal, Layout.edge)

            if let overview = detail?.overview ?? preview.overview, !overview.isEmpty {
                Text(overview)
                    .font(.subheadline)
                    .lineSpacing(3)
                    .foregroundStyle(Color.screen.opacity(0.88))
                    .frame(maxWidth: 680, alignment: .leading)
                    .padding(.horizontal, Layout.edge)
            }
        }
    }

    /// Request on a line of its own, as Play is on a library title's page; Trailer and Watchlist underneath. Requested
    /// is a button too: it asks, then takes the request back for the household, as music's does.
    private var actions: some View {
        let state = model.titleState(title)
        return VStack(spacing: 18) {
            VStack(alignment: .leading, spacing: 6) {
                requestButton(state)
                if seerrPaused, state == .requestable || state == .requested {
                    Text(TitleWords.seerrNotAnswering).font(.footnote).foregroundStyle(Color.usher)
                } else if case .refused(let reason) = outcome, state == .requestable {
                    Text(reason).font(.footnote).foregroundStyle(Color.usher)
                } else if case .failed(let reason) = outcome, state == .requestable {
                    Text(reason).font(.footnote).foregroundStyle(Color.usher)
                }
            }

            HStack(alignment: .top, spacing: 8) {
                if trailer != nil {
                    Button { showingTrailer = true } label: { Label(t(Words.trailer), systemImage: Symbol.trailer) }
                }
                if model.hasWatchlist, let onList = onWatchlist ?? detail?.onWatchlist {
                    Button {
                        Task {
                            if await model.setOnWatchlist(WatchlistKey(title), !onList) != nil {
                                onWatchlist = !onList
                                await load() // adding can request it too
                            }
                        }
                    } label: {
                        Label(t(Words.watchlist), systemImage: onList ? Symbol.onWatchlist : Symbol.watchlist)
                    }
                    .accessibilityLabel(onList ? "Remove from watchlist" : "Add to watchlist")
                }
            }
            .buttonStyle(LabeledActionStyle())
        }
        .frame(maxWidth: 560, alignment: .leading)
    }

    /// "R   2026   1h 59m   Movie".
    private var facts: [String] {
        guard let detail else { return [title.year.map(String.init), title.isShow ? "Show" : "Movie"].compactMap { $0 } }
        let length: String? = detail.isShow
            ? detail.seasons.map { $0 == 1 ? "1 season" : "\($0) seasons" }
            : detail.runtimeMinutes.flatMap { Format.runtime(minutes: $0) }
        let status = detail.status.flatMap { ["Released", "Ended"].contains($0) ? nil : $0 }
        return [detail.certification, detail.year.map(String.init), length, detail.isShow ? "Show" : "Movie", status].compactMap { $0 }
    }

    @ViewBuilder
    private func sections(_ detail: ExternalDetail) -> some View {
        if !detail.cast.isEmpty {
            VStack(alignment: .leading, spacing: 10) {
                SectionHeading(title: "Cast")
                ScrollView(.horizontal) {
                    LazyHStack(alignment: .top, spacing: 14) {
                        ForEach(Array(detail.cast.enumerated()), id: \.offset) { _, person in
                            castFace(person)
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                }
                .scrollIndicators(.hidden)
            }
        }
        if !detail.madeBy.isEmpty || detail.studio != nil {
            HStack(alignment: .top, spacing: 32) {
                if !detail.madeBy.isEmpty { labeled(detail.isShow ? "Created by" : "Directed by", detail.madeBy.joined(separator: ", ")) }
                if let studio = detail.studio { labeled(detail.isShow ? "Network" : "Studio", studio) }
            }
            .padding(.horizontal, Layout.edge)
        }
        if !detail.recommendations.isEmpty {
            VStack(alignment: .leading, spacing: 10) {
                SectionHeading(title: "If you like this")
                ScrollView(.horizontal) {
                    LazyHStack(alignment: .top, spacing: Layout.rowSpacing) {
                        ForEach(detail.recommendations) { other in
                            OutsideTitleCard(title: other, width: Layout.posterWidth(sizeClass))
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                }
                .scrollIndicators(.hidden)
            }
        }
    }

    /// Not a button: people outside the library have nowhere to go.
    private func castFace(_ person: ExternalPerson) -> some View {
        VStack(spacing: 6) {
            RemoteImage(url: model.client?.imageURL(person.image, width: 200)) {
                ZStack {
                    Color.seat
                    Text(person.name.split(separator: " ").prefix(2).compactMap(\.first).map(String.init).joined())
                        .font(.marquee(22, weight: .bold, scales: false))
                        .foregroundStyle(Color.usher)
                }
            }
            .frame(width: 84, height: 84)
            .clipShape(Circle())
            VStack(spacing: 1) {
                Text(person.name).font(.caption.weight(.semibold)).foregroundStyle(Color.screen)
                if let character = person.character { Text(character).font(.caption2).foregroundStyle(Color.usher) }
            }
            .lineLimit(1)
            .frame(width: 96)
        }
        .accessibilityElement(children: .combine)
    }

    private func labeled(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption.weight(.semibold)).foregroundStyle(Color.usher)
            Text(value).font(.subheadline).foregroundStyle(Color.screen.opacity(0.85))
        }
    }

    @ViewBuilder
    private func requestButton(_ state: TitleRequestState) -> some View {
        switch state {
        case .requestable:
            if case .refused = outcome {
                Label(TitleWords.cantRequest, systemImage: Symbol.request)
                    .frame(maxWidth: .infinity)
                    .modifier(DisabledAction())
            } else {
                Button {
                    guard !requesting else { return }
                    Task { await request() }
                } label: {
                    Label(requesting ? "Requesting…" : outcome == nil ? TitleWords.request : TitleWords.tryAgain, systemImage: Symbol.request)
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(ActionButtonStyle(prominent: true))
                .disabled(requesting || seerrPaused)
            }
        case .requested:
            Button { confirmingUndo = true } label: {
                Label(TitleWords.requested, systemImage: Symbol.requested)
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(ActionButtonStyle())
            .disabled(requesting || seerrPaused)
            .accessibilityHint(TitleWords.unrequestHint)
            .accessibilityAddTraits(.isSelected)
            .confirmationDialog(TitleWords.unrequest(title.title), isPresented: $confirmingUndo, titleVisibility: .visible) {
                Button(TitleWords.unrequestAction, role: .destructive) { Task { await unrequest() } }
                Button(TitleWords.keepIt, role: .cancel) {}
            }
        case .partlyAvailable, .inLibrary, .none:
            Label(title.availability.label ?? "Not available", systemImage: state == .inLibrary ? Symbol.watched : Symbol.requested)
                .frame(maxWidth: .infinity)
                .modifier(DisabledAction())
        }
    }

    private func request() async {
        requesting = true
        defer { requesting = false }
        let result = await model.requestTitle(mediaType: title.mediaType, tmdbId: title.tmdbId)
        outcome = result == .sent ? nil : result
    }

    private func unrequest() async {
        requesting = true
        defer { requesting = false }
        outcome = nil
        if let failure = await model.unrequestTitle(mediaType: title.mediaType, tmdbId: title.tmdbId) {
            model.requestError = failure
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        let status = await model.titleRequestStatus()
        seerrPaused = status.configured && !status.reachable
        do {
            detail = try await client.externalTitle(preview)
            error = nil
        } catch FlexarrClientError.http(status: 404, _) {
            error = "Flexarr can’t find that title."
        } catch {
            if detail == nil { self.error = ConnectionError(error).message }
        }
    }
}

/// A request button with nothing to press (in the library, partly in, or Can't request), drawn as the button is.
private struct DisabledAction: ViewModifier {
    func body(content: Content) -> some View {
        Button {} label: { content }
            .buttonStyle(ActionButtonStyle())
            .disabled(true)
    }
}
