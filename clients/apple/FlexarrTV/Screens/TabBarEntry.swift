import FlexarrKit
import SwiftUI

/// A tab between Home and Discover: a library, or Live TV once the server has it set up (Settings › Connections on
/// the web). Their order is chosen in Settings › Sidebar and kept with the profile, so its other devices follow it
/// (servers before 0.54 keep none: then it's kept on this Apple TV); Home, Discover, Search and Settings stay put.
enum TabBarEntry: Identifiable, Hashable {
    case library(Library)
    case liveTv

    /// This Apple TV's own order, from before profiles kept one; still used with servers that don't.
    static let storageKey = "FlexarrTabBarOrder"

    var id: String {
        switch self {
        case .library(let library): library.id
        case .liveTv: LibraryOrder.liveTv
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
            case .audiobooks: "headphones"
            case .homeVideos: "folder"
            default: "square.stack"
            }
        case .liveTv: "antenna.radiowaves.left.and.right"
        }
    }

    /// What's in the sidebar, in the server's order, then Live TV. Podcasts are on iPhone, iPad and the web, not here;
    /// recordings are in Live TV › Recordings, so their library has no tab of its own while Live TV has one.
    @MainActor
    static func available(model: AppModel) -> [TabBarEntry] {
        // A type this app doesn't know (from a newer server) gets no tab rather than a film grid.
        model.libraries.filter { $0.type != .podcasts && $0.type != .unknown && !(model.hasLiveTv && isRecordings($0)) }
            .map(TabBarEntry.library)
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

    /// The tabs in the profile's order, or with an older server the one kept on this Apple TV in `stored` (ids, one a
    /// line); any not in it yet, such as a new library, at the end.
    @MainActor
    static func ordered(model: AppModel, stored: String) -> [TabBarEntry] {
        LibraryOrder.sorted(available(model: model), by: model.libraryOrder ?? saved(stored), id: \.id)
    }

    /// Puts the tabs in this order: for the profile, or with an older server on this Apple TV.
    @MainActor
    static func save(_ entries: [TabBarEntry], model: AppModel, stored: inout String) {
        if model.libraryOrder != nil {
            model.setLibraryOrder(entries.map(\.id))
        } else {
            stored = entries.map(\.id).joined(separator: "\n")
        }
    }

    /// A profile that has no order yet takes the one chosen on this Apple TV before profiles kept one, so the sidebar
    /// stays as it was and its other devices follow it.
    @MainActor
    static func adoptStoredOrder(model: AppModel, stored: String) {
        guard model.libraryOrder?.isEmpty == true, !model.libraries.isEmpty else { return }
        let order = saved(stored)
        guard !order.isEmpty else { return }
        model.setLibraryOrder(LibraryOrder.sorted(available(model: model), by: order, id: \.id).map(\.id))
    }

    private static func saved(_ stored: String) -> [String] {
        stored.split(separator: "\n").map(String.init)
    }
}
