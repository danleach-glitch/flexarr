import FlexarrKit
import Foundation

/// How the watchlist is put in order.
enum WatchlistSort: String, CaseIterable {
    case status, added, release, title

    var label: String {
        switch self {
        case .status: "Ready first"
        case .added: "Recently added"
        case .release: "Release date"
        case .title: "Title"
        }
    }

    func sorted(_ entries: [WatchlistEntry]) -> [WatchlistEntry] {
        switch self {
        case .status: entries.sorted { ($0.status.rank, $1.addedAt) < ($1.status.rank, $0.addedAt) }
        case .added: entries.sorted { $0.addedAt > $1.addedAt }
        case .release: entries.sorted { $0.releaseKey < $1.releaseKey }
        case .title: entries.sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }
        }
    }
}

extension WatchlistStatus {
    /// Ready first, then what's on its way, then what's coming, then the rest.
    var rank: Int {
        switch self {
        case .ready: 0
        case .onItsWay: 1
        case .coming: 2
        case .notAvailable: 3
        case .unknown: 4
        }
    }
}

extension WatchlistEntry {
    /// For sorting by release: the library title's date, the date Seerr gave, or the year.
    var releaseKey: String { item?.releaseDate ?? releaseDate ?? year.map { "\($0)-01-01" } ?? "9999" }

    /// "On Netflix", or the services it's on; nil when none are known.
    var streamingLabel: String? {
        guard let streaming, !streaming.isEmpty else { return nil }
        return streaming.count == 1 ? "On \(streaming[0])" : streaming.joined(separator: ", ")
    }
}
