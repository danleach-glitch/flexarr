import FlexarrKit
import SwiftUI

/// The profile's watchlist: what's ready to play as its usual card, everything else as Discover shows it, with where
/// it's got to (and where it streams) under the title. Pressing and holding a card takes it off.
struct WatchlistView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @State private var entries: [WatchlistEntry]?
    @State private var error: String?
    @State private var showing: WatchlistStatus?
    /// "movie", "tv", or nil for both.
    @State private var kind: String?
    @State private var sort = WatchlistSort.status
    @FocusState private var focusedItem: String?

    private var columns: [GridItem] {
        let spacing = (1920 - 2 * Layout.edge - 6 * Layout.posterWidth) / 5
        return Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: spacing, alignment: .top), count: 6)
    }

    private var shown: [WatchlistEntry] {
        sort.sorted((entries ?? []).filter { (showing == nil || $0.status == showing) && (kind == nil || $0.mediaType == kind) })
    }

    private static let statuses: [(WatchlistStatus?, String)] = [
        (nil, "Everything"), (.ready, "Ready to watch"), (.onItsWay, "On its way"), (.coming, "Coming"), (.notAvailable, "Not available"),
    ]

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 30) {
                Text("Watchlist")
                    .font(.marquee(64, weight: .heavy))
                    .foregroundStyle(Color.screen)
                if let entries, entries.isEmpty {
                    StatusView(state: .empty("Nothing here yet. Add films and shows from their pages or Discover.")).frame(height: 500)
                } else if entries != nil {
                    filters
                    if shown.isEmpty {
                        StatusView(state: .empty("Nothing on your watchlist matches.")).frame(height: 300)
                    }
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 64) {
                        ForEach(shown) { entry in
                            card(entry)
                        }
                    }
                    .focusSection()
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 500)
                } else {
                    StatusView(state: .loading).frame(height: 500)
                }
            }
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .background(RoomBackground(ambient: entries?.first?.item?.glowColor, intensity: 0.18))
        .task { await load() }
        .onReceive(NotificationCenter.default.publisher(for: .watchlistChanged)) { _ in Task { await load() } }
    }

    private var filters: some View {
        HStack(spacing: 18) {
            ForEach(Self.statuses, id: \.1) { status, label in
                Button(label) { showing = status }
                    .buttonStyle(PillButtonStyle(selected: showing == status))
            }
            Menu {
                Picker("Kind", selection: $kind) {
                    Text("Films and shows").tag(String?.none)
                    Text("Films").tag(String?.some("movie"))
                    Text("Shows").tag(String?.some("tv"))
                }
                Picker("Sort", selection: $sort) {
                    ForEach(WatchlistSort.allCases, id: \.self) { Text($0.label).tag($0) }
                }
            } label: {
                Label(sort.label, systemImage: "line.3.horizontal.decrease")
            }
            .buttonStyle(PillButtonStyle(selected: kind != nil || sort != .status))
        }
        .focusSection()
    }

    @ViewBuilder
    private func card(_ entry: WatchlistEntry) -> some View {
        if let item = entry.item {
            // Its own menu offers Remove from watchlist.
            PosterCard(item: item, focus: $focusedItem) { router.push(.item(item)) }
        } else if let title = entry.external {
            ExternalCard(title: title, focus: $focusedItem, focusID: entry.id, note: note(entry))
        }
    }

    /// Where it's got to, then where it streams: "On its way · On Netflix".
    private func note(_ entry: WatchlistEntry) -> String {
        let status: String? = switch entry.status {
        case .onItsWay: "On its way"
        case .coming: entry.releaseDate.flatMap(Self.out) ?? "Coming"
        case .notAvailable: entry.year.map(String.init)
        default: nil
        }
        return [status, entry.streamingLabel].compactMap { $0 }.joined(separator: " · ")
    }

    /// "Out 5 Mar 2027" (a calendar day, read and shown in UTC).
    private static func out(_ raw: String) -> String? {
        guard let date = try? Date(raw, strategy: .iso8601.year().month().day()) else { return nil }
        var style = Date.FormatStyle(date: .abbreviated, time: .omitted)
        style.timeZone = TimeZone(identifier: "UTC") ?? .gmt
        return "Out \(date.formatted(style))"
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            entries = try await client.watchlist()
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}

/// On a title's page: puts it on the watchlist or takes it off. One button throughout, so focus never lands nowhere.
/// `onRequested` hears when adding it also requested it.
struct WatchlistActionButton: View {
    @Environment(AppModel.self) private var model
    let key: WatchlistKey
    let on: Bool
    var ambient: Color = .silver
    var onRequested: () -> Void = {}
    @State private var changed: Bool?

    var body: some View {
        let onList = changed ?? on
        Button {
            Task {
                guard let requested = await model.setOnWatchlist(key, !onList) else { return }
                changed = !onList
                if requested { onRequested() }
            }
        } label: {
            Label(onList ? "On watchlist" : "Watchlist", systemImage: onList ? "bookmark.fill" : "bookmark")
        }
        .buttonStyle(ActionButtonStyle(ambient: ambient))
        .accessibilityLabel(onList ? "Remove from watchlist" : "Add to watchlist")
    }
}
