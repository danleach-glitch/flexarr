import SwiftUI

/// What a film or TV library's tab opens on, for every library on this Apple TV.
struct LibrarySettingsView: View {
    @AppStorage(LibraryOpening.storageKey) private var opensOn = LibraryOpening.browse

    var body: some View {
        SettingsPage(title: "Libraries") {
            SettingsGroupLabel("Open on")
            ForEach(LibraryOpening.allCases, id: \.self) { opening in
                SettingsRow(title: opening.label, symbol: opening == .browse ? "rectangle.stack" : "square.grid.3x3",
                            help: opening.help, accessory: opensOn == opening ? .checkmark : .none) {
                    opensOn = opening
                }
            }
        }
    }
}

private extension LibraryOpening {
    var help: String {
        switch self {
        case .browse: "A library opens on its highlights: what you're watching in it, the latest, the best not yet watched and its biggest genres."
        case .all: "A library opens on every title, A to Z, with sorting, genres and a letter rail to jump through it."
        }
    }
}
