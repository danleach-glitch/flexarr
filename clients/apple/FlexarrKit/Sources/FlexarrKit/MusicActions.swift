import Foundation

/// The buttons on a music artist's or album's page, in the order every client shares (docs/music-requests.md): Play,
/// Shuffle, Radio, Download, Follow or Request album, then ⋯ for the rest. Only those that apply to the page: an
/// outside page (or a followed artist with no albums yet) has nothing to play. Android's `core` has the same rules and
/// the web's music pages the same order; change them together. The web labels each button; the apps spell out Play
/// and show the rest as round icons, spelling out Follow or Request album when it's the page's only button (iPhone).
public struct MusicActions: Equatable, Sendable {
    public enum Kind: Sendable { case artist, album }
    public enum Platform: Sendable { case phone, tv }

    public enum Button: Equatable, Sendable {
        /// Something in the library to play.
        case play
        case shuffle
        /// Artists in the library only: them and artists like them.
        case radio
        /// Phones only, an album in the library.
        case download
        /// Follow, or Following (pressing it offers to stop): artists, with music requests on.
        case follow
        /// Request album, or Requested (pressing it offers to cancel): albums the library lacks, with music requests on.
        case request
        /// ⋯, holding `menu`; there only when the menu has something in it.
        case more
    }

    /// What ⋯ holds.
    public enum MenuItem: Equatable, Sendable {
        /// Delete the album or artist (the main profile; the web's for now).
        case delete
    }

    public let buttons: [Button]
    public let menu: [MenuItem]

    /// - Parameters:
    ///   - hasMusic: there's something in the library to play (false on an outside page, and for a placeholder artist,
    ///     followed before their first album arrives).
    ///   - canDownload: this device can download the album (phones).
    ///   - canAsk: music requests are on for this profile (`MusicRequestStatus.configured`) and the page has something
    ///     to ask for: an artist to follow, or an album the library lacks.
    ///   - canDelete: the profile may delete it here.
    public init(kind: Kind, platform: Platform, hasMusic: Bool, canDownload: Bool = false, canAsk: Bool, canDelete: Bool = false) {
        var menu: [MenuItem] = []
        if canDelete && hasMusic { menu.append(.delete) }

        var buttons: [Button] = []
        if hasMusic {
            buttons.append(.play)
            buttons.append(.shuffle)
            if kind == .artist { buttons.append(.radio) }
            if kind == .album && platform == .phone && canDownload { buttons.append(.download) }
        }
        if canAsk { buttons.append(kind == .artist ? .follow : .request) }
        if !menu.isEmpty { buttons.append(.more) }

        self.buttons = buttons
        self.menu = menu
    }

    public func has(_ button: Button) -> Bool { buttons.contains(button) }
    public func has(_ item: MenuItem) -> Bool { menu.contains(item) }
}
