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
    /// On screen: changes elsewhere reload it now; otherwise when it next appears (the tab stays alive while hidden).
    @State private var isVisible = false
    @State private var stale = false

    private var columns: [GridItem] {
        let spacing = (1920 - 2 * Layout.edge - 6 * Layout.posterWidth) / 5
        return Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: spacing, alignment: .top), count: 6)
    }

    /// The list as filtered and sorted, kept rather than worked out on every focus move.
    @State private var shown: [WatchlistEntry] = []

    private func reshape() {
        shown = sort.sorted((entries ?? []).filter { (showing == nil || $0.status == showing) && (kind == nil || $0.mediaType == kind) })
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
                    StatusView(state: .empty("Nothing here yet.")).frame(height: 500)
                } else if entries != nil {
                    filters
                    if let error {
                        Label(error, systemImage: "exclamationmark.triangle")
                            .font(.system(size: 24, weight: .medium))
                            .foregroundStyle(Color.usher)
                    }
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
        .onAppear {
            isVisible = true
            if stale { Task { await load() } }
        }
        .onDisappear { isVisible = false }
        .onReceive(NotificationCenter.default.publisher(for: .watchlistChanged)) { _ in
            if isVisible { Task { await load() } } else { stale = true }
        }
        // A card taken off while focused: focus moves to the one that took its place, not wherever the focus engine likes.
        .onChange(of: entries) { _, _ in reshape() }
        .onChange(of: showing) { _, _ in reshape() }
        .onChange(of: kind) { _, _ in reshape() }
        .onChange(of: sort) { _, _ in reshape() }
        .onChange(of: shown.map(focusID)) { old, new in
            guard let focused = focusedItem, !new.contains(focused), let at = old.firstIndex(of: focused), !new.isEmpty else { return }
            focusedItem = new[min(at, new.count - 1)]
        }
    }

    /// The id a card takes focus by: the library title's for a ready one (PosterCard's), else the entry's.
    private func focusID(_ entry: WatchlistEntry) -> String { entry.item?.id ?? entry.id }

    private var filters: some View {
        HStack(spacing: 18) {
            ForEach(Self.statuses, id: \.1) { status, label in
                Button(label) { showing = status }
                    .buttonStyle(PillButtonStyle(selected: showing == status))
            }
            Menu {
                Picker("Kind", selection: $kind) {
                    Text("Movies and shows").tag(String?.none)
                    Text("Movies").tag(String?.some("movie"))
                    Text("Shows").tag(String?.some("tv"))
                }
                Picker("Sort", selection: $sort) {
                    ForEach(WatchlistSort.allCases, id: \.self) { Text($0.label).tag($0) }
                }
            } label: {
                // The kind shows here too, so a filtered list says why shows (or films) are missing.
                Label([kind == "movie" ? "Movies" : kind == "tv" ? "Shows" : nil, sort.label].compactMap { $0 }.joined(separator: ", "),
                      systemImage: "line.3.horizontal.decrease")
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
            // Its menu offers Remove, and Request for a title nobody has asked for.
            ExternalCard(title: title, focus: $focusedItem, focusID: entry.id, note: note(entry),
                         onRequest: entry.status == .notAvailable ? { Task { await request(title) } } : nil)
        } else {
            // In the library once, and not now (or hidden from this profile), with no TMDB id to look it up by.
            Button {} label: {
                VStack(alignment: .leading, spacing: 12) {
                    Color.seat
                        .frame(width: Layout.posterWidth, height: Layout.posterWidth * 1.5)
                        .overlay { Text(entry.title).font(.marquee(30, weight: .bold)).foregroundStyle(Color.screen.opacity(0.8)).multilineTextAlignment(.center).padding(16) }
                        .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
                    Text(entry.title).font(.system(size: 24, weight: .semibold)).lineLimit(1)
                }
            }
            .buttonStyle(.card)
            .focused($focusedItem, equals: entry.id)
            .contextMenu {
                if let id = entry.libraryItemId {
                    Button(role: .destructive) { Task { await model.setOnWatchlist(.library(id), false) } } label: {
                        Label("Remove from watchlist", systemImage: "bookmark.slash")
                    }
                }
            }
        }
    }

    private func request(_ title: ExternalTitle) async {
        do {
            try await model.client?.request(title)
            await load()
        } catch {
            model.requestError = ConnectionError(error).message
        }
    }

    /// Where it's got to, then where it streams: "On its way   On Netflix", "Request   On Prime Video".
    private func note(_ entry: WatchlistEntry) -> String {
        let status: String? = switch entry.status {
        case .onItsWay: "On its way"
        case .coming: entry.releaseDate.flatMap(Self.out) ?? "Coming"
        // As the other apps' Request button: pressing the card opens its page, where Request is.
        case .notAvailable: "Request"
        default: nil
        }
        return [status, entry.streamingLabel].compactMap { $0 }.joined(separator: "   ")
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
        stale = false
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
    var iconOnly = false
    var onRequested: () -> Void = {}
    /// What was pressed here, until the page's own value changes (it reloads after something is watched, say).
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
            Label("Watchlist", systemImage: onList ? "bookmark.fill" : "bookmark")
        }
        .buttonStyle(ActionButtonStyle(ambient: ambient, iconOnly: iconOnly))
        .accessibilityLabel(onList ? "Remove from watchlist" : "Add to watchlist")
        .onChange(of: on) { _, _ in changed = nil }
        // Watching something can take it off while the page keeps the same value: the next refresh tells.
        .onChange(of: model.watchlistItems) { _, _ in changed = nil }
    }
}
