import FlexarrKit
import SwiftUI

/// A film or TV library's Genres: a tile for each of its genres, biggest first, with a backdrop from one of its titles.
/// Choosing one opens the library's titles in that genre on top, so Back comes back to the tiles.
struct LibraryGenresView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.focusesContent) private var arriving
    let library: Library
    @Binding var view: LibraryOpening

    @State private var genres: [GenreCount]?
    @State private var error: String?
    @FocusState private var focused: String?

    var body: some View {
        ZStack {
            RoomBackground(ambient: nil)
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 50) {
                    LibraryViewSwitch(library: library, view: $view).focusSection()
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
                            GenreTile(name: genre.name, caption: caption(genre), backdrop: genre.backdrop, focus: $focused) {
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
        let noun = library.type == .tvShows ? (genre.count == 1 ? "show" : "shows") : (genre.count == 1 ? "movie" : "movies")
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
            genres = try await client.libraryGenres(library.id)
            error = nil
        } catch {
            if genres == nil { self.error = ConnectionError(error).message }
        }
    }
}
