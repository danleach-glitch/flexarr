import FlexarrKit
import SwiftUI

/// The profile's watchlist: what's ready to play as its usual card, and where everything else has got to, with a
/// request for anything nobody has asked for. Long press takes a title off.
struct WatchlistView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var entries: [WatchlistEntry]?
    @State private var error: String?
    @State private var showing: WatchlistStatus?
    /// "movie", "tv", or nil for both.
    @State private var kind: String?
    @State private var sort = WatchlistSort.status
    /// On screen: changes elsewhere reload it now; otherwise when it next appears.
    @State private var isVisible = false
    @State private var stale = false

    private var columns: [GridItem] {
        let minimum: CGFloat = sizeClass == .regular ? 140 : 100
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.45), spacing: 12, alignment: .top)]
    }

    private var shown: [WatchlistEntry] {
        sort.sorted((entries ?? []).filter { (showing == nil || $0.status == showing) && (kind == nil || $0.mediaType == kind) })
    }

    private var filtered: Bool { showing != nil || kind != nil }

    var body: some View {
        Group {
            if let entries, entries.isEmpty {
                StatusView(state: .empty("Nothing here yet."))
            } else if let entries {
                ScrollView {
                    if let error {
                        // The list below is what was last loaded.
                        Label(error, systemImage: "exclamationmark.triangle")
                            .font(.footnote)
                            .foregroundStyle(Color.silver)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.horizontal, Layout.edge)
                    }
                    if shown.isEmpty, !entries.isEmpty {
                        StatusView(state: .empty("Nothing on your watchlist matches.")).frame(height: 300)
                    }
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                        ForEach(shown) { entry in
                            WatchlistCard(entry: entry) { Task { await load() } }
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                    .padding(.vertical, 12)
                }
                .refreshable { await load() }
            } else if let error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .background(RoomBackground(ambient: nil))
        .navigationTitle("Watchlist")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Menu {
                    Picker("Show", selection: $showing) {
                        Text("Everything").tag(WatchlistStatus?.none)
                        Text("Ready to watch").tag(WatchlistStatus?.some(.ready))
                        Text("On its way").tag(WatchlistStatus?.some(.onItsWay))
                        Text("Coming").tag(WatchlistStatus?.some(.coming))
                        Text("Not available").tag(WatchlistStatus?.some(.notAvailable))
                    }
                    Picker("Kind", selection: $kind) {
                        Text("Films and shows").tag(String?.none)
                        Text("Films").tag(String?.some("movie"))
                        Text("Shows").tag(String?.some("tv"))
                    }
                    Picker("Sort", selection: $sort) {
                        ForEach(WatchlistSort.allCases, id: \.self) { Text($0.label).tag($0) }
                    }
                } label: {
                    Label("Filter", systemImage: filtered ? "line.3.horizontal.decrease.circle.fill" : "line.3.horizontal.decrease.circle")
                }
            }
        }
        .task { await load() }
        .onAppear {
            isVisible = true
            if stale { Task { await load() } }
        }
        .onDisappear { isVisible = false }
        .onReceive(NotificationCenter.default.publisher(for: .watchlistChanged)) { _ in
            if isVisible { Task { await load() } } else { stale = true }
        }
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

/// One title on the watchlist: the library's card when it's ready (its menu takes it off), otherwise its poster and
/// where it's got to.
private struct WatchlistCard: View {
    @Environment(AppModel.self) private var model
    let entry: WatchlistEntry
    let onChange: () -> Void
    @State private var requesting = false

    private var key: WatchlistKey? {
        entry.tmdbId.map { .tmdb(mediaType: entry.mediaType, id: $0) } ?? entry.libraryItemId.map(WatchlistKey.library)
    }

    var body: some View {
        if let item = entry.item {
            PosterCard(item: item)
        } else {
            VStack(alignment: .leading, spacing: 6) {
                Color.seat
                    .aspectRatio(2 / 3, contentMode: .fit)
                    .overlay {
                        RemoteImage(url: model.client?.imageURL(entry.poster, width: 342)) {
                            Text(entry.title)
                                .font(.marquee(18, weight: .bold, scales: false))
                                .foregroundStyle(Color.screen.opacity(0.8))
                                .multilineTextAlignment(.center)
                                .padding(8)
                        }
                    }
                    .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
                VStack(alignment: .leading, spacing: 1) {
                    Text(entry.title)
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                    Text([entry.isShow ? "Show" : "Film", entry.year.map(String.init)].compactMap { $0 }.joined(separator: ", "))
                        .font(.caption)
                        .foregroundStyle(Color.usher)
                    if let streaming = entry.streamingLabel {
                        Text(streaming)
                            .font(.caption)
                            .foregroundStyle(Color.usher)
                    }
                }
                .lineLimit(1)
                .accessibilityElement(children: .combine)
                status
            }
            .contextMenu {
                if let key {
                    Button(role: .destructive) { Task { await model.setOnWatchlist(key, false) } } label: {
                        Label("Remove from watchlist", systemImage: "bookmark.slash")
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var status: some View {
        switch entry.status {
        case .onItsWay:
            Text("On its way").font(.caption.weight(.semibold)).foregroundStyle(Color.silver)
        case .coming:
            Text(comingLabel).font(.caption.weight(.semibold)).foregroundStyle(Color.silver)
        case .notAvailable:
            if let title = entry.external {
                Button {
                    requesting = true
                    Task {
                        do {
                            try await model.client?.request(title)
                        } catch {
                            model.requestError = ConnectionError(error).message
                        }
                        requesting = false
                        onChange()
                    }
                } label: {
                    Text("Request").font(.caption.weight(.semibold))
                }
                .buttonStyle(.bordered)
                .controlSize(.small)
                .disabled(requesting)
            }
        default:
            EmptyView()
        }
    }

    /// "Out 5 Mar 2027", in the phone's own format (the date is a calendar day, so it's read and shown in UTC).
    private var comingLabel: String {
        guard let raw = entry.releaseDate, let date = try? Date(raw, strategy: .iso8601.year().month().day()) else { return "Coming" }
        var style = Date.FormatStyle(date: .abbreviated, time: .omitted)
        style.timeZone = TimeZone(identifier: "UTC") ?? .gmt
        return "Out \(date.formatted(style))"
    }
}
