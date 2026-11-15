import Foundation
import Security

/// The `flexarr://` links Flexarr makes for itself (the Top Shelf's Play, a podcast widget, a Siri shortcut) carry a
/// random key only this app and its extensions know, so they can be told from a link another app or a web page opened:
/// any of those can open `flexarr://play/…`, but not with the key. One without it never starts anything playing and
/// never switches profile unasked (see `fromOutside`), as the Android app's `AppLink.received` treats such a link.
public enum OwnLinks {
    /// The query item the key goes in.
    public static let queryName = "k"

    private static let account = "link-key"

    /// This device's key: made the first time it's asked for, and kept beside the shared sign-in, where the Top Shelf
    /// can read it too. Where the Keychain can't keep it (an unsigned build), the one made stays for as long as the
    /// app runs, so a link the app signs and the check it then makes still agree.
    public static var key: String {
        if let kept = SharedSignIn.read(account: account).flatMap({ String(data: $0, encoding: .utf8) }), kept.count >= 32 { return kept }
        return made
    }

    /// Made once per process, and kept in the Keychain when it can be.
    private static let made: String = {
        var bytes = [UInt8](repeating: 0, count: 32)
        let made = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess
            ? bytes.map { String(format: "%02x", $0) }.joined()
            : (UUID().uuidString + UUID().uuidString).replacingOccurrences(of: "-", with: "").lowercased()
        SharedSignIn.write(Data(made.utf8), account: account)
        return made
    }()

    /// `url` with `key` added (replacing any it had).
    public static func signed(_ url: URL, key: String) -> URL {
        guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return url }
        components.queryItems = (components.queryItems ?? []).filter { $0.name != queryName } + [URLQueryItem(name: queryName, value: key)]
        return components.url ?? url
    }

    /// Whether `url` carries `key`.
    public static func isSigned(_ url: URL, key: String) -> Bool {
        guard !key.isEmpty, let given = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?
            .first(where: { $0.name == queryName })?.value else { return false }
        let a = Array(given.utf8), b = Array(key.utf8)
        guard a.count == b.count else { return false }
        return zip(a, b).reduce(UInt8(0)) { $0 | ($1.0 ^ $1.1) } == 0
    }

    /// `url` without the key, as the rest of the app reads it.
    public static func unsigned(_ url: URL) -> URL {
        guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false), let items = components.queryItems,
              items.contains(where: { $0.name == queryName }) else { return url }
        let kept = items.filter { $0.name != queryName }
        components.queryItems = kept.isEmpty ? nil : kept
        return components.url ?? url
    }

    /// What a link from outside the app may do: a title to play opens its page instead (where Play is one press away),
    /// and a channel to put on does nothing. Everything else (a title's page, the watchlist, a family invite) opens as
    /// it is; a profile it names is asked about first (see `AppModel.open`). Nil for a link that's dropped.
    public static func fromOutside(_ url: URL) -> URL? {
        guard url.scheme == "flexarr", var components = URLComponents(url: unsigned(url), resolvingAgainstBaseURL: false) else { return url }
        switch components.host {
        case "play":
            components.host = "item"
            // Where to start, which subtitles and shuffling mean nothing to a page: only the profile goes along.
            let profile = components.queryItems?.filter { $0.name == "profile" } ?? []
            components.queryItems = profile.isEmpty ? nil : profile
            return components.url
        case "live":
            return nil
        default:
            return components.url
        }
    }
}
