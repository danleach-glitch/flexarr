import FlexarrKit
import SwiftUI

/// The screen for each place a stack can go: shared by the iPhone's tabs and the Mac's sidebar.
struct RouteDestination: View {
    let route: Route

    var body: some View {
        switch route {
        case .item(let item) where item.kind.isMusic:
            MusicDestination(item: item)
        case .item(let item) where item.kind == .audiobook:
            BookView(id: item.id, preview: item)
        case .item(let item) where item.kind == .podcast:
            PodcastView(id: item.id, preview: item)
        case .item(let item):
            DetailView(id: item.id, preview: item)
        case .itemID(let id):
            DetailView(id: id, preview: nil)
        case .person(let id, let name):
            PersonView(id: id, name: name)
        case .library(let library, _, _) where library.type == .unknown:
            // A library of a type this app doesn't know (from a newer server): nothing to show it with.
            EmptyView()
        case .library(let library, _, let genre) where library.type == .audiobooks:
            // A genre opens a screen of its own, above the library's (Back goes to its genres).
            BooksLibraryView(library: library, genre: genre)
        case .bookSeries(let id, let name):
            BookSeriesView(id: id, name: name)
        case .library(let library, _, _) where library.type == .podcasts:
            PodcastsView()
        case .library(let library, let sort, let genre):
            LibraryView(library: library, initialSort: sort, initialGenre: genre)
        case .albums(let library, let sort):
            LibraryView(library: library, initialSort: sort, opening: .albums)
        case .artists(let library):
            LibraryView(library: library, opening: .all)
        case .playlist(let playlist):
            PlaylistView(playlist: playlist)
        case .podcasts:
            PodcastsView()
        case .podcastBrowse:
            PodcastBrowseView()
        case .liveTv:
            LiveTvView()
        case .watchlist:
            WatchlistView()
        case .comingSoon:
            ComingSoonView()
        case .musicArtist(let artist):
            ExternalArtistView(preview: artist)
        case .musicAlbum(let album):
            ExternalAlbumView(preview: album)
        case .musicItem(let id, let isArtist):
            MusicItemDestination(id: id, isArtist: isArtist)
        case .external(let title):
            ExternalTitleView(preview: title)
        case .genre, .musicGenre, .bookList:
            // Discover, and books' See all pages, are the Apple TV's; nothing on iPhone or iPad goes here.
            EmptyView()
        }
    }
}
