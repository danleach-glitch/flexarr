import Foundation

/// A title's YouTube trailer (`ItemDetail.trailerYouTubeId`), where it plays: embedded on the phone, in the YouTube
/// app on the TV. Only a real video id reaches either address: the id comes from metadata Flexarr doesn't control.
public struct YouTubeTrailer: Sendable, Hashable, Identifiable {
    public let id: String

    /// Nil unless `id` is a YouTube video id (eleven letters, digits, - or _), as the web's TrailerDialog checks.
    public init?(id: String?) {
        guard let id, id.count == 11, id.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }) else {
            return nil
        }
        self.id = id
    }

    /// The privacy-enhanced embed, playing straight away and inline, with no other channels' videos at the end.
    public var embedURL: URL {
        URL(string: "https://www.youtube-nocookie.com/embed/\(id)?autoplay=1&rel=0&playsinline=1")!
    }

    /// The YouTube app on Apple TV (its scheme is in the TV app's LSApplicationQueriesSchemes).
    public var appURL: URL { URL(string: "youtube://watch/\(id)")! }

    /// A page holding the embed. YouTube refuses an embed that doesn't say which site it's on (error 153), so load
    /// this with a base URL of `embedOrigin`, which the frame then sends as its referrer, and never a page path.
    public var embedPage: String {
        """
        <!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1">
        <style>html,body{margin:0;height:100%;background:#000}iframe{position:fixed;inset:0;width:100%;height:100%;border:0}</style>
        </head><body><iframe src="\(embedURL.absoluteString)" allow="autoplay; encrypted-media; picture-in-picture; fullscreen"
        referrerpolicy="strict-origin-when-cross-origin" allowfullscreen></iframe></body></html>
        """
    }

    public static let embedOrigin = URL(string: "https://flexarr.io")!
}
