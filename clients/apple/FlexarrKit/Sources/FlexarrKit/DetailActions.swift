import Foundation

/// The buttons on a film's, show's or episode's page, in the order every client shares (docs/design.md › Platforms):
/// Play or Resume, Start over, Shuffle, Trailer, Watched, Watchlist, Download, then ⋯ for the rest. Android's `core`
/// has the same rules, and the web's `ItemPage` the same order; change them together.
public struct DetailActions: Equatable, Sendable {
    public enum Kind: Sendable { case movie, show, episode }
    public enum Platform: Sendable { case phone, tv }

    public enum Button: Equatable, Sendable {
        /// Play, Resume from 32:10 or Resume S1E3 (the label is the page's to work out).
        case play
        /// Only once there's progress.
        case startOver
        /// Shows only.
        case shuffle
        /// Only with a trailer, not for children, and only where one can play (the TVs need the YouTube app).
        case trailer
        /// Mark as watched or Mark as unwatched, a toggle. Not for children: their history keeps itself.
        case watched
        /// On or off the watchlist. Not for children.
        case watchlist
        /// Phones only, when the title can be downloaded.
        case download
        /// ⋯, holding `menu`; there only when the menu has something in it.
        case more
    }

    /// What ⋯ holds: only things the apps already do.
    public enum MenuItem: Equatable, Sendable {
        /// Part way through and not watched: forget the place without marking it watched (it leaves Continue
        /// watching). The Watched button only marks it watched then, so this is never the same thing twice; once it's
        /// watched, Watched itself becomes Mark as unwatched and this goes.
        case markUnwatched
        /// Search for subtitles.
        case subtitles
        /// An episode's show.
        case goToShow
    }

    public let buttons: [Button]
    public let menu: [MenuItem]

    /// - Parameters:
    ///   - hasProgress: part watched and not finished.
    ///   - canPlayTrailers: whether a trailer can play here (on a TV, whether the YouTube app is installed).
    ///   - canDownload: whether this title can be downloaded to this device (phones).
    ///   - canSearchSubtitles: whether the server can find subtitles for it.
    public init(kind: Kind, hasProgress: Bool, watched: Bool, hasTrailer: Bool, isKids: Bool, platform: Platform,
                canPlayTrailers: Bool, canDownload: Bool, canSearchSubtitles: Bool = false) {
        var menu: [MenuItem] = []
        if !isKids && hasProgress && !watched { menu.append(.markUnwatched) }
        if !isKids && canSearchSubtitles { menu.append(.subtitles) }
        if kind == .episode { menu.append(.goToShow) }

        var buttons: [Button] = [.play]
        if hasProgress { buttons.append(.startOver) }
        if kind == .show { buttons.append(.shuffle) }
        if hasTrailer && !isKids && canPlayTrailers { buttons.append(.trailer) }
        if !isKids { buttons.append(.watched) }
        if !isKids { buttons.append(.watchlist) }
        if platform == .phone && canDownload { buttons.append(.download) }
        if !menu.isEmpty { buttons.append(.more) }

        self.buttons = buttons
        self.menu = menu
    }

    public func has(_ button: Button) -> Bool { buttons.contains(button) }
    public func has(_ item: MenuItem) -> Bool { menu.contains(item) }
}
