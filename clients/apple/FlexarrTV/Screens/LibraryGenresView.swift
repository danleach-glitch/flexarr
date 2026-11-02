import FlexarrKit
import SwiftUI

/// A film, TV or audiobooks library's Genres: a tile for each of its genres, with a backdrop from one of its titles (a
/// film's or show's genres, biggest first) or its newest covers fanned (a book's, A to Z, as on the phones and the web).
/// Choosing one opens the library's titles in that genre on top, so Back comes back to the tiles. A music library's
/// count albums (their own genres, or their artist's when they have none), with an artist's fanart, and open the
/// genre's albums.
struct LibraryGenresView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.focusesContent) private var arriving
    let library: Library
    var view: Binding<LibraryOpening> = .constant(.genres)
    /// A music library's switch, in place of `view`.
    var musicView: Binding<MusicOpening>?
    /// An audiobooks library's switch, in place of `view`.
    var bookView: Binding<BookOpening>?
    var offersAlbums = true

    @State private var genres: [GenreCount]?
    @State private var error: String?
    @FocusState private var focused: String?

    var body: some View {
        ZStack {
            RoomBackground(ambient: nil)
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 50) {
                    Group {
                        if let musicView {
                            MusicViewSwitch(view: musicView, offersAlbums: offersAlbums)
                        } else if let bookView {
                            BookViewSwitch(view: bookView)
                        } else {
                            LibraryViewSwitch(library: library, view: view)
                        }
                    }
                    .focusSection()
                    content
                }
                .padding(.horizontal, Layout.edge)
                .padding(.top, LibraryViewSwitch.topInset)
                .padding(.bottom, 80)
            }
            .scrollClipDisabled()
            .ignoresSafeArea(edges: [.top, .horizontal])
        }
        .task(id: library.id) { await load() }
        // Choosing the library in the sidebar puts focus on the switch; the first genre takes it, as a title does in
        // the other views.
        .onChange(of: arriving.wrappedValue) { passFocusOnArrival() }
        .onChange(of: genres?.first?.name) { passFocusOnArrival() }
    }

    @ViewBuilder
    private var content: some View {
        if let genres, !genres.isEmpty {
            // Not lazy: Down can only move to tiles that are there, and a library has a few dozen genres at most.
            Grid(alignment: .topLeading, horizontalSpacing: GenreTile.spacing, verticalSpacing: 44) {
                ForEach(Array(stride(from: 0, to: genres.count, by: GenreTile.across)), id: \.self) { start in
                    GridRow {
                        ForEach(genres[start..<min(start + GenreTile.across, genres.count)], id: \.name) { genre in
                            GenreTile(name: genre.name, caption: caption(genre), backdrop: genre.backdrop,
                                      covers: library.type == .audiobooks ? genre.covers ?? [] : nil, focus: $focused) {
                                router.push(.library(library, .title, genre: genre.name))
                            }
                        }
                    }
                }
            }
            .focusSection()
        } else {
            Group {
                if genres != nil {
                    StatusView(state: .empty("No genres here yet. They come with each title's details once a scan has found them."))
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } })
                } else {
                    StatusView(state: .loading)
                }
            }
            .frame(maxWidth: .infinity, minHeight: 600)
        }
    }

    private func caption(_ genre: GenreCount) -> String {
        if library.type == .music { return genre.count == 1 ? "1 album" : "\(genre.count) albums" }
        let noun = switch library.type {
        case .tvShows: genre.count == 1 ? "show" : "shows"
        case .audiobooks: genre.count == 1 ? "book" : "books"
        default: genre.count == 1 ? "movie" : "movies"
        }
        return "\(genre.count) \(noun)"
    }

    private func passFocusOnArrival() {
        guard arriving.wrappedValue, let first = genres?.first else { return }
        focused = first.name
        arriving.wrappedValue = false
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            genres = switch library.type {
            case .music: try await client.libraryAlbumGenres(library.id) ?? []
            case .audiobooks: try await client.bookGenres(library.id)
            default: try await client.libraryGenres(library.id)
            }
            error = nil
        } catch {
            if genres == nil { self.error = ConnectionError(error).message }
        }
    }
}
