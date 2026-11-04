import FlexarrKit
import SwiftUI

/// What a film or TV library's tab opens on, an audiobooks library's and a music library's, for every library on this
/// Apple TV.
struct LibrarySettingsView: View {
    @AppStorage(LibraryOpening.storageKey) private var opensOn = LibraryOpening.browse
    @AppStorage(MusicOpening.storageKey) private var musicOpensOn = MusicOpening.browse
    @AppStorage(BookOpening.storageKey) private var booksOpenOn: BookOpening?

    var body: some View {
        SettingsPage(title: t(Words.libraries)) {
            SettingsGroupLabel(t(Words.moviesAndShows))
            ForEach(LibraryOpening.allCases, id: \.self) { opening in
                SettingsRow(title: opening.label, symbol: opening.symbol,
                            help: opening.help, accessory: opensOn == opening ? .checkmark : .none) {
                    opensOn = opening
                }
            }
            SettingsGroupLabel(t("Books"))
            let books = BookOpening.opening(saved: booksOpenOn, library: opensOn)
            ForEach(BookOpening.allCases, id: \.self) { opening in
                SettingsRow(title: opening.label, symbol: opening.symbol,
                            help: opening.help, accessory: books == opening ? .checkmark : .none) {
                    booksOpenOn = opening
                }
            }
            SettingsGroupLabel(t(Words.music))
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
        case .browse: t("A library opens on its highlights: what you’re watching in it, the latest, the best not yet watched and its biggest genres.")
        case .all: t("A library opens on every title, A to Z, with sorting, genres and a letter rail to jump through it.")
        case .genres: t("A library opens on its genres, each with a picture from its titles. Home videos open on Browse.")
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
        case .browse: t("The books you’re listening to, then your shelves.")
        case .all: t("Every book, A to Z, with sorting, filters and a letter rail.")
        case .genres: t("A tile for each genre.")
        case .series: t("Every series, A to Z.")
        case .authors: t("Every author, A to Z.")
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
        case .browse: t("The album you played last, then your rows.")
        case .artists: t("Every artist, A to Z.")
        case .albums: t("Every album, A to Z.")
        case .genres: t("A tile for each genre.")
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
