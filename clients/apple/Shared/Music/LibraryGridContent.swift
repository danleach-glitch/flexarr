import FlexarrKit
import Foundation

/// What a library grid lists: a library's titles (a music library's are its artists), or a music library's albums.
enum LibraryGridContent {
    case titles
    case artists
    case albums

    var sorts: [LibrarySort] {
        switch self {
        case .titles: LibrarySort.allCases
        case .artists: [.title, .added]
        case .albums: [.title, .added, .year]
        }
    }

    func count(_ total: Int) -> String {
        let noun = switch self {
        case .titles: total == 1 ? "title" : "titles"
        case .artists: total == 1 ? "artist" : "artists"
        case .albums: total == 1 ? "album" : "albums"
        }
        return "\(total.formatted()) \(noun)"
    }
}
