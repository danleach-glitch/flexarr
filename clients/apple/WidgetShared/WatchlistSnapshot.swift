import Foundation

/// What the Watchlist widget shows, as the iPhone app last saw it: the titles on the watchlist that are ready to play,
/// newest on the list first. The app writes it to the app group when the watchlist may have changed; the widget only
/// reads it, and never calls the server.
struct WatchlistSnapshot: Codable, Equatable {
    struct Title: Codable, Equatable, Identifiable {
        let id: String
        let title: String
        /// A small poster in the app group's folder, when one was saved.
        let posterFile: String?
    }

    var ready: [Title]
    var updatedAt: Date

    static let empty = WatchlistSnapshot(ready: [], updatedAt: .distantPast)

    static let widgetKind = "io.github.flexarr.watchlist"

    private static var file: URL? { PodcastSnapshot.folder?.appending(path: "watchlist.json") }

    static func load() -> WatchlistSnapshot {
        guard let file, let data = try? Data(contentsOf: file),
              let snapshot = try? JSONDecoder().decode(WatchlistSnapshot.self, from: data) else { return .empty }
        return snapshot
    }

    func save() {
        guard let folder = PodcastSnapshot.folder, let file = Self.file else { return }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        if let data = try? JSONEncoder().encode(self) { try? data.write(to: file, options: .atomic) }
    }

    /// The link a poster opens: the title's page in the app.
    static func itemURL(_ id: String) -> URL {
        URL(string: "flexarr://item/\(id)") ?? URL(string: "flexarr://")!
    }
}
