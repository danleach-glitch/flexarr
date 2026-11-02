import Foundation

/// The buttons on an audiobook's page, in the order every client shares (docs/design.md › Platforms): Play or Resume,
/// Start over, Finished (or Unread once it's finished), Download, then ⋯ for the rest. Like `DetailActions` for films
/// and shows. Android's `core` has the same rules and the web's `BookPage` the same order; change them together.
/// Part way through, Mark as unread is offered in Start over's question rather than as a button of its own.
public struct BookActions: Equatable, Sendable {
    public enum Platform: Sendable { case phone, tv }

    public enum Button: Equatable, Sendable {
        /// Play, Resume or Pause (the label is the page's to work out).
        case play
        /// Only with somewhere to start over from: part way through (it asks first), or finished.
        case startOver
        /// Mark as finished, or Mark as unread once it's finished. Not for children: their history keeps itself.
        case finished
        /// Phones only, when the book can be downloaded.
        case download
        /// ⋯; there only when it has something in it.
        case more
    }

    public let buttons: [Button]

    /// - Parameters:
    ///   - hasProgress: a place part way through, not finished.
    ///   - canDownload: this device can download the book (phones).
    ///   - hasMenu: the page has a ⋯ menu with something in it.
    public init(hasProgress: Bool, finished: Bool, isKids: Bool, platform: Platform, canDownload: Bool = false, hasMenu: Bool = false) {
        var buttons: [Button] = [.play]
        if hasProgress || finished { buttons.append(.startOver) }
        if !isKids { buttons.append(.finished) }
        if platform == .phone && canDownload { buttons.append(.download) }
        if hasMenu { buttons.append(.more) }
        self.buttons = buttons
    }

    public func has(_ button: Button) -> Bool { buttons.contains(button) }
}
