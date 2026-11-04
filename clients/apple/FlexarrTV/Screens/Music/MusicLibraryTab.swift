import FlexarrKit
import SwiftUI

/// Which of a music library's views its tab opens on: Browse (the hero and rows), Artists, Albums or Genres.
/// Settings › Libraries, for every music library on this Apple TV.
enum MusicOpening: String, CaseIterable {
    case browse
    case artists
    case albums
    case genres

    static let storageKey = "FlexarrMusicOpensOn"

    var label: String {
        switch self {
        case .browse: t(Words.browse)
        case .artists: t(Words.artists)
        case .albums: t(Words.albums)
        case .genres: t(Words.genres)
        }
    }

    /// Albums and Genres need a server from 0.59; before it, Browse and Artists.
    var needsAlbums: Bool { self == .albums || self == .genres }
}

/// A music library's tab, as a film or TV library's: Browse, Artists, Albums and Genres, switched with the words at the
/// top. Settings › Libraries › Opens on picks the first.
struct MusicLibraryTab: View {
    @Environment(AppModel.self) private var model
    let library: Library
    @AppStorage(MusicOpening.storageKey) private var opensOn = MusicOpening.browse
    @State private var picked: MusicOpening?
    @State private var switching = false
    @State private var arriving = false
    /// Whether the server lists albums (and so has Albums and Genres): from 0.59, asked once per server. Until it
    /// answers, Browse and Artists.
    private var offersAlbums: Bool { model.offersMusicAlbums == true }

    var body: some View {
        let view = Binding { shown(picked ?? opensOn) } set: {
            switching = $0 != shown(picked ?? opensOn)
            picked = $0
        }
        Group {
            // Opening on Albums or Genres waits for the answer, rather than showing Browse first.
            if (picked ?? opensOn).needsAlbums, model.offersMusicAlbums == nil {
                StatusView(state: .loading).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                switch view.wrappedValue {
                case .browse: MusicLibraryView(library: library, view: view, offersAlbums: offersAlbums)
                case .artists: LibraryView(library: library, content: .artists, musicSwitch: view, offersAlbums: offersAlbums)
                case .albums: LibraryView(library: library, content: .albums, musicSwitch: view, offersAlbums: offersAlbums)
                case .genres: LibraryGenresView(library: library, musicView: view, offersAlbums: offersAlbums)
                }
            }
        }
        .environment(\.focusesViewSwitch, $switching)
        .environment(\.focusesContent, $arriving)
        .onChange(of: opensOn) { picked = nil }
        .task(id: library.id) { await model.checkMusicAlbums(library: library) }
    }

    private func shown(_ view: MusicOpening) -> MusicOpening { view.needsAlbums && !offersAlbums ? .browse : view }
}

/// "Browse", "Artists", "Albums" and "Genres" at the top-left of a music library's views, as for films and shows.
struct MusicViewSwitch: View {
    @Binding var view: MusicOpening
    var offersAlbums = true
    var focusChanged: (Bool) -> Void = { _ in }

    var body: some View {
        ViewSwitch(choices: MusicOpening.allCases.filter { offersAlbums || !$0.needsAlbums }.map { ($0, $0.label) },
                   selection: $view, focusChanged: focusChanged)
    }
}
