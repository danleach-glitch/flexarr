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

    /// What the grid lists, as in "No Rock albums here." Prefer `noneInGenre(_:)`, which says the whole sentence.
    var noun: String {
        switch self {
        case .titles: t("titles")
        case .artists: t("artists")
        case .albums: t("albums")
        }
    }

    /// "No Rock albums here.", for a genre with nothing in it.
    func noneInGenre(_ genre: String) -> String {
        switch self {
        case .titles: t("No {genre} titles here.", ["genre": genre])
        case .artists: t("No {genre} artists here.", ["genre": genre])
        case .albums: t("No {genre} albums here.", ["genre": genre])
        }
    }

    func count(_ total: Int) -> String {
        switch self {
        case .titles: t("{count, plural, one {# title} other {# titles}}", ["count": total])
        case .artists: t("{count, plural, one {# artist} other {# artists}}", ["count": total])
        case .albums: t("{count, plural, one {# album} other {# albums}}", ["count": total])
        }
    }
}
