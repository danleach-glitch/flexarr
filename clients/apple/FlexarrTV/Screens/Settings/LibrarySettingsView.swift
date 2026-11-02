import SwiftUI

/// What a film, TV or audiobooks library's tab opens on, and a music library's, for every library on this Apple TV.
struct LibrarySettingsView: View {
    @AppStorage(LibraryOpening.storageKey) private var opensOn = LibraryOpening.browse
    @AppStorage(MusicOpening.storageKey) private var musicOpensOn = MusicOpening.browse

    var body: some View {
        SettingsPage(title: "Libraries") {
            SettingsGroupLabel("Movies, shows and books")
            ForEach(LibraryOpening.allCases, id: \.self) { opening in
                SettingsRow(title: opening.label, symbol: opening.symbol,
                            help: opening.help, accessory: opensOn == opening ? .checkmark : .none) {
                    opensOn = opening
                }
            }
            SettingsGroupLabel("Music")
            ForEach(MusicOpening.allCases, id: \.self) { opening in
                SettingsRow(title: opening.label, symbol: opening.symbol,
                            help: opening.help, accessory: musicOpensOn == opening ? .checkmark : .none) {
                    musicOpensOn = opening
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
        case .genres: "A library opens on its genres, each with a picture from its titles. Home videos open on Browse."
        }
    }

    var symbol: String {
        switch self {
        case .browse: "rectangle.stack"
        case .all: "square.grid.3x3"
        case .genres: "theatermasks"
        }
    }
}

private extension MusicOpening {
    var help: String {
        switch self {
        case .browse: "The album you played last, then your rows."
        case .artists: "Every artist, A to Z."
        case .albums: "Every album, A to Z."
        case .genres: "A tile for each genre."
        }
    }

    var symbol: String {
        switch self {
        case .browse: "rectangle.stack"
        case .artists: "person.2"
        case .albums: "square.grid.3x3"
        case .genres: "guitars"
        }
    }
}
