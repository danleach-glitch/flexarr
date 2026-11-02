import SwiftUI

/// What a film or TV library's tab opens on, an audiobooks library's and a music library's, for every library on this
/// Apple TV.
struct LibrarySettingsView: View {
    @AppStorage(LibraryOpening.storageKey) private var opensOn = LibraryOpening.browse
    @AppStorage(MusicOpening.storageKey) private var musicOpensOn = MusicOpening.browse
    @AppStorage(BookOpening.storageKey) private var booksOpenOn: BookOpening?

    var body: some View {
        SettingsPage(title: "Libraries") {
            SettingsGroupLabel("Movies and shows")
            ForEach(LibraryOpening.allCases, id: \.self) { opening in
                SettingsRow(title: opening.label, symbol: opening.symbol,
                            help: opening.help, accessory: opensOn == opening ? .checkmark : .none) {
                    opensOn = opening
                }
            }
            SettingsGroupLabel("Books")
            let books = BookOpening.opening(saved: booksOpenOn, library: opensOn)
            ForEach(BookOpening.allCases, id: \.self) { opening in
                SettingsRow(title: opening.label, symbol: opening.symbol,
                            help: opening.help, accessory: books == opening ? .checkmark : .none) {
                    booksOpenOn = opening
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

private extension BookOpening {
    var help: String {
        switch self {
        case .browse: "The books you're listening to, then your shelves."
        case .all: "Every book, A to Z, with sorting, filters and a letter rail."
        case .genres: "A tile for each genre."
        case .series: "Every series, A to Z."
        case .authors: "Every author, A to Z."
        }
    }

    var symbol: String {
        switch self {
        case .browse: "rectangle.stack"
        case .all: "square.grid.3x3"
        case .genres: "theatermasks"
        case .series: "books.vertical"
        case .authors: "person.2"
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
