import FlexarrKit
import SwiftUI

/// A tab between Home and Search: a library, or Live TV once the server has it set up (Settings › Connections on
/// the web). Their order is chosen in Settings › Sidebar and kept on this Apple TV; Home, Search and Settings stay put.
enum TabBarEntry: Identifiable, Hashable {
    case library(Library)
    case liveTv

    static let storageKey = "FlexarrTabBarOrder"

    var id: String {
        switch self {
        case .library(let library): library.id
        case .liveTv: "live-tv"
        }
    }

    var tab: TabID {
        switch self {
        case .library(let library): .library(library.id)
        case .liveTv: .liveTv
        }
    }

    var title: String {
        switch self {
        case .library(let library): library.name
        case .liveTv: "Live TV"
        }
    }

    var symbol: String {
        switch self {
        case .library(let library):
            switch library.type {
            case .movies: "film"
            case .tvShows: "tv"
            case .music: "music.note"
            default: "square.stack"
            }
        case .liveTv: "antenna.radiowaves.left.and.right"
        }
    }

    /// What's in the sidebar, in the server's order, then Live TV. Podcasts are on iPhone, iPad and the web, not here;
    /// recordings are in Live TV › Recordings, so their library has no tab of its own while Live TV has one.
    @MainActor
    static func available(model: AppModel) -> [TabBarEntry] {
        model.libraries.filter { $0.type != .podcasts && !(model.hasLiveTv && isRecordings($0)) }.map(TabBarEntry.library)
            + (model.hasLiveTv ? [.liveTv] : [])
    }

    /// The library the server makes for the DVR's recordings when the first one's done (Dvr.EnsureLibraryAsync): a TV
    /// library called Recordings.
    static func isRecordings(_ library: Library) -> Bool {
        library.type == .tvShows && library.name == "Recordings"
    }

    /// The libraries with a tab, where a link or Go to can open a title.
    @MainActor
    static func libraries(model: AppModel) -> [Library] {
        available(model: model).compactMap { if case .library(let library) = $0 { library } else { nil } }
    }

    /// The tabs in the order kept in `stored` (ids, one a line); any not in it yet, such as a new library, at the end.
    @MainActor
    static func ordered(model: AppModel, stored: String) -> [TabBarEntry] {
        ordered(available(model: model), stored: stored)
    }

    static func ordered(_ entries: [TabBarEntry], stored: String) -> [TabBarEntry] {
        let saved = stored.split(separator: "\n").map(String.init)
        let rank = Dictionary(saved.enumerated().map { ($1, $0) }, uniquingKeysWith: { first, _ in first })
        let known = entries.filter { rank[$0.id] != nil }.sorted { rank[$0.id]! < rank[$1.id]! }
        return known + entries.filter { rank[$0.id] == nil }
    }

    static func storing(_ entries: [TabBarEntry]) -> String {
        entries.map(\.id).joined(separator: "\n")
    }
}
