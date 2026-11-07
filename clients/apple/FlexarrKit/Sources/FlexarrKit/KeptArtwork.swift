import Foundation

/// One name for a picture whichever of the server's two addresses it came from. Artwork's address starts with the
/// address in use, so the same poster has one address at home and another away from home; kept under either, a phone
/// that browsed at home would download every poster again over mobile data the moment it left. So pictures are kept
/// under their home address (`atHome`), in memory and on disk (`KeptArtwork`).
extension ServerAddresses {
    private static let homesLock = NSLock()
    /// Each server's away-from-home address and its home address, both as far as the path ("https://mini.ts.net:8443").
    nonisolated(unsafe) private static var homes: [String: String] = [:]

    /// An address as far as its path: the scheme, host and port, as written.
    private static func root(of url: URL) -> String? {
        let text = url.absoluteString
        guard let scheme = text.range(of: "://") else { return nil }
        let end = text[scheme.upperBound...].firstIndex { $0 == "/" || $0 == "?" || $0 == "#" } ?? text.endIndex
        return String(text[..<end])
    }

    /// Notes that `remote` and `home` are the same server (every `ServerAddresses` made with both does).
    static func pair(remote: URL, home: URL) {
        guard let away = root(of: remote), let atHome = root(of: home), away != atHome else { return }
        homesLock.withLock { homes[away] = atHome }
    }

    /// `url` as it's written at home: the same path and query at the server's home address, when `url` is at a
    /// server's address for away from home. Anything else (the home address already, another site's) as it is.
    public static func atHome(_ url: URL) -> URL {
        guard let away = root(of: url), let home = homesLock.withLock({ homes[away] }) else { return url }
        return URL(string: home + url.absoluteString.dropFirst(away.count)) ?? url
    }
}

/// Artwork fetched away from home, kept on disk where the same picture fetched at home is (`URLCache`, under its
/// home address), so each is downloaded and stored once whichever address was in use.
public enum KeptArtwork {
    /// The picture kept under `url` (a home address), if the server said it stays good this long: nil when there's
    /// none, or it has to be asked for again.
    public static func data(for url: URL, in cache: URLCache = .shared, now: Date = Date()) -> Data? {
        guard let kept = cache.cachedResponse(for: URLRequest(url: url)), let response = kept.response as? HTTPURLResponse,
              response.statusCode == 200, isGood(response, now: now), !kept.data.isEmpty else { return nil }
        return kept.data
    }

    /// Keeps a picture fetched from the server's other address under `url` (its home address), with what the server
    /// said about how long it's good for. One the server wants asked for each time isn't kept.
    public static func keep(_ data: Data, from response: HTTPURLResponse, as url: URL, in cache: URLCache = .shared, now: Date = Date()) {
        guard response.statusCode == 200, !data.isEmpty, let lifetime = lifetime(of: response), lifetime > 0 else { return }
        var headers: [String: String] = [:]
        for name in ["Cache-Control", "Content-Type", "ETag", "Last-Modified"] {
            if let value = response.value(forHTTPHeaderField: name) { headers[name] = value }
        }
        // When it was fetched, which its age is counted from (the server's own clock may be wrong).
        headers["Date"] = httpDate.string(from: now)
        headers["Content-Length"] = String(data.count)
        guard let kept = HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: headers) else { return }
        cache.storeCachedResponse(CachedURLResponse(response: kept, data: data), for: URLRequest(url: url))
    }

    /// Whether a kept answer is still inside the time the server gave it.
    static func isGood(_ response: HTTPURLResponse, now: Date) -> Bool {
        guard let lifetime = lifetime(of: response), let sent = response.value(forHTTPHeaderField: "Date").flatMap(httpDate.date(from:)) else { return false }
        let age = now.timeIntervalSince(sent)
        // Dated in the future (a clock put back since): its age isn't known.
        return age > -300 && age < lifetime
    }

    /// How long the server said an answer may be kept without asking again (Cache-Control's max-age); nil when it
    /// gave none, or said to ask each time.
    static func lifetime(of response: HTTPURLResponse) -> TimeInterval? {
        guard let control = response.value(forHTTPHeaderField: "Cache-Control")?.lowercased() else { return nil }
        var lifetime: TimeInterval?
        for directive in control.split(separator: ",").map({ $0.trimmingCharacters(in: .whitespaces) }) {
            if directive == "no-cache" || directive == "no-store" { return nil }
            if directive.hasPrefix("max-age="), let seconds = TimeInterval(directive.dropFirst("max-age=".count)) { lifetime = seconds }
        }
        return lifetime
    }

    /// Dates as HTTP writes them: "Fri, 06 Nov 2026 12:00:00 GMT".
    private static let httpDate: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "GMT")
        formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss 'GMT'"
        return formatter
    }()
}
