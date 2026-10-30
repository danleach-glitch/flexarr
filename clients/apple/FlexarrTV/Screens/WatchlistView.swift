import FlexarrKit
import SwiftUI

/// The profile's watchlist: what's ready to play as its usual card, everything else as Discover shows it (a title
/// that's on its way says "Requested"). Pressing and holding takes a title off.
struct WatchlistView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @State private var entries: [WatchlistEntry]?
    @State private var error: String?
    @FocusState private var focusedItem: String?

    private var columns: [GridItem] {
        let spacing = (1920 - 2 * Layout.edge - 6 * Layout.posterWidth) / 5
        return Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: spacing, alignment: .top), count: 6)
    }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 30) {
                Text("Watchlist")
                    .font(.marquee(64, weight: .heavy))
                    .foregroundStyle(Color.screen)
                if let entries, entries.isEmpty {
                    StatusView(state: .empty("Nothing here yet. Add films and shows from their pages or Discover.")).frame(height: 500)
                } else if let entries {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 64) {
                        ForEach(entries) { entry in
                            card(entry)
                                .contextMenu {
                                    Button(role: .destructive) { Task { await model.setOnWatchlist(key(entry), false) } } label: {
                                        Label("Remove from watchlist", systemImage: "bookmark.slash")
                                    }
                                }
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

    @ViewBuilder
    private func card(_ entry: WatchlistEntry) -> some View {
        if let item = entry.item {
            PosterCard(item: item, focus: $focusedItem) { router.push(.item(item)) }
        } else if let title = entry.external {
            ExternalCard(title: title, focus: $focusedItem, focusID: entry.id)
        }
    }

    private func key(_ entry: WatchlistEntry) -> WatchlistKey {
        entry.tmdbId.map { .tmdb(mediaType: entry.mediaType, id: $0) } ?? .library(entry.libraryItemId ?? entry.id)
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
