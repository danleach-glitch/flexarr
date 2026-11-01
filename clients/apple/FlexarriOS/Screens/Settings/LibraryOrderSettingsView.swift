import FlexarrKit
import SwiftUI

/// A place in the Libraries tab: a library, Podcasts or Live TV, in the order the profile chose.
enum LibraryMenuEntry: Identifiable, Hashable {
    case library(Library)
    /// Podcasts are one place however many libraries hold them; `id` is their library's, when there is one.
    case podcasts(id: String)
    case liveTv

    var id: String {
        switch self {
        case .library(let library): library.id
        case .podcasts(let id): id
        case .liveTv: LibraryOrder.liveTv
        }
    }

    var title: String {
        switch self {
        case .library(let library): library.name
        case .podcasts: "Podcasts"
        case .liveTv: "Live TV"
        }
    }

    var symbol: String {
        switch self {
        case .library(let library):
            switch library.type {
            case .tvShows: "tv"
            case .music: "music.note"
            case .audiobooks: "headphones"
            case .homeVideos: "folder"
            default: "film"
            }
        case .podcasts: "mic.fill"
        case .liveTv: "antenna.radiowaves.left.and.right"
        }
    }

    /// The Libraries tab's places in the profile's order (Settings › Library order); any it doesn't mention yet, such
    /// as a new library, after them.
    @MainActor
    static func ordered(model: AppModel) -> [LibraryMenuEntry] {
        // A type this app doesn't know (from a newer server) has no screen here: skipped, not shown as films.
        var entries = model.libraries.filter { $0.type != .podcasts && $0.type != .unknown }.map(LibraryMenuEntry.library)
        if model.podcastStatus != nil {
            entries.append(.podcasts(id: model.libraries.first(where: { $0.type == .podcasts })?.id ?? "podcasts"))
        }
        if model.hasLiveTv { entries.append(.liveTv) }
        return LibraryOrder.sorted(entries, by: model.libraryOrder ?? [], id: \.id)
    }
}

/// The order of the profile's libraries in the Libraries tab, and in its other devices' menus: dragged by the handles.
struct LibraryOrderSettingsView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        let entries = LibraryMenuEntry.ordered(model: model)
        List {
            Section {
                ForEach(entries) { entry in
                    Label(entry.title, systemImage: entry.symbol)
                        .listRowBackground(Color.seat)
                }
                .onMove { from, to in
                    var order = entries
                    order.move(fromOffsets: from, toOffset: to)
                    model.setLibraryOrder(order.map(\.id))
                }
            } footer: {
                Text("\(model.currentProfile?.name ?? "This profile")’s own. The web, the Apple TV and Android list your libraries in this order too.")
            }
        }
        .environment(\.editMode, .constant(.active))
        .scrollContentBackground(.hidden)
        .background(RoomBackground(ambient: nil))
        .navigationTitle("Library order")
        .navigationBarTitleDisplayMode(.inline)
    }
}
