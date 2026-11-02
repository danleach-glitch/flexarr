import Foundation

/// What pressing and holding a piece of music offers (a card in a row, or a track in a tracklist), in the order every
/// app shares: Play, Shuffle, Play next, Add to queue, Add to playlist, Go to artist, Download. Only those that apply.
/// The page's own ⋯ is `MusicActions.menu`. Android's `core` has the same rules; change them together.
public struct MusicMenu: Equatable, Sendable {
    public enum Subject: Sendable { case artist, album, track, playlist }
    public enum Place: Sendable {
        /// A card in a row or grid.
        case card
        /// A track in an album's or playlist's tracklist.
        case row
    }

    public enum Item: Equatable, Sendable {
        case play
        /// Artists, albums and playlists.
        case shuffle
        case playNext
        case addToQueue
        /// Phones only; not a playlist into another.
        case addToPlaylist
        /// Albums and tracks whose artist is known.
        case goToArtist
        /// Phones, an album or playlist this device can download.
        case download
    }

    public let items: [Item]

    /// - Parameters:
    ///   - hasArtist: the artist's id is known (and worth going to: not on their own page).
    ///   - canDownload: this device can download it.
    public init(subject: Subject, place: Place, platform: MusicActions.Platform, hasArtist: Bool = false, canDownload: Bool = false) {
        var items: [Item] = [.play]
        if subject != .track && place == .card { items.append(.shuffle) }
        items += [.playNext, .addToQueue]
        if platform == .phone && subject != .playlist { items.append(.addToPlaylist) }
        if hasArtist && (subject == .album || subject == .track) { items.append(.goToArtist) }
        if platform == .phone && canDownload && place == .card && (subject == .album || subject == .playlist) { items.append(.download) }
        self.items = items
    }

    public func has(_ item: Item) -> Bool { items.contains(item) }
}
