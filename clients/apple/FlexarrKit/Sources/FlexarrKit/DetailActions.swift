import Foundation

/// The buttons on a film's, show's or episode's page, in the order every client shares (docs/design.md › Platforms):
/// Play or Resume, Start over, Shuffle, Trailer, Watched, Watchlist, Download, then ⋯ for the rest. Android's `core`
/// has the same rules, and the web's `ItemPage` the same order; change them together.
///
/// The phones show at most `phoneSlots` buttons beside Play (plus ⋯), so the row fits like an episode's: Start over and
/// Download always stay, and the rest fill what's left by where you are with the title. Before you start: Trailer,
/// Watchlist, then Watched; once you've started or watched it: Watched, Watchlist, then Trailer; Shuffle last. Whatever
/// doesn't fit moves to the top of ⋯, in the same order.
public struct DetailActions: Equatable, Sendable {
    public enum Kind: Sendable { case movie, show, episode }
    public enum Platform: Sendable { case phone, tv }

    public enum Button: Hashable, Sendable {
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
    public enum MenuItem: Hashable, Sendable {
        /// A button that didn't fit on a phone's row: Shuffle, Trailer, Watched (Mark as watched or unwatched) or
        /// Watchlist (Add to or Remove from watchlist). Start over and Download never move.
        case button(Button)
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
    ///   - canWatchlist: whether this title can go on a watchlist here (films and shows, on servers that have one).
    public init(kind: Kind, hasProgress: Bool, watched: Bool, hasTrailer: Bool, isKids: Bool, platform: Platform,
                canPlayTrailers: Bool, canDownload: Bool, canSearchSubtitles: Bool = false, canWatchlist: Bool = true) {
        var row: [Button] = []
        if hasProgress { row.append(.startOver) }
        if kind == .show { row.append(.shuffle) }
        if hasTrailer && !isKids && canPlayTrailers { row.append(.trailer) }
        if !isKids { row.append(.watched) }
        if !isKids && canWatchlist { row.append(.watchlist) }
        if platform == .phone && canDownload { row.append(.download) }

        var moved: [MenuItem] = []
        if platform == .phone && row.count > Self.phoneSlots {
            let pinned = row.filter { $0 == .startOver || $0 == .download }
            let started = hasProgress || watched
            let priority: [Button] = started ? [.watched, .watchlist, .trailer, .shuffle] : [.trailer, .watchlist, .watched, .shuffle]
            let fill = priority.filter(row.contains).prefix(max(0, Self.phoneSlots - pinned.count))
            let kept = Set(pinned + fill)
            moved = row.filter { !kept.contains($0) }.map(MenuItem.button)
            row = row.filter(kept.contains)
        }

        var menu = moved
        if !isKids && canSearchSubtitles { menu.append(.subtitles) }
        if kind == .episode { menu.append(.goToShow) }

        self.buttons = [.play] + row + (menu.isEmpty ? [] : [.more])
        self.menu = menu
    }

    /// How many buttons a phone's row holds beside Play and ⋯.
    public static let phoneSlots = 3

    public func has(_ button: Button) -> Bool { buttons.contains(button) }
    public func has(_ item: MenuItem) -> Bool { menu.contains(item) }
}
