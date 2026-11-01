import Foundation

/// Where a server is reached: the address it was connected with (usually on the home network) and, once the server
/// has remote access turned on, its address for away from home (over Tailscale). Requests go to whichever answered
/// last. A GET that can't connect there, or hasn't heard back after a moment, is also sent to the other address and
/// the first answer wins; a write only moves over once the first address has failed to connect, so it's never sent
/// twice. While away, home is tried again every few minutes so the app goes back to it once it's reachable.
///
/// Shared by every client made for the same sign-in (see `shared(home:remote:)`), so what one request learns, the
/// next one uses.
public final class ServerAddresses: @unchecked Sendable {
    public let home: URL
    public let remote: URL?

    /// How long to wait for the current address before also asking the other one (GETs only).
    public static let alternateDelay: Duration = .milliseconds(2500)
    /// How long the reachability check gives the address in use before asking the other one too.
    public static let probeHeadStart: Duration = .milliseconds(1500)
    /// While away, how often home is tried again.
    public static let homeCheckInterval: TimeInterval = 5 * 60

    private let lock = NSLock()
    private var _current: URL
    private var homeCheckedAt: Date?

    public init(home: URL, remote: URL?) {
        self.home = home
        self.remote = remote == home ? nil : remote
        _current = home
    }

    /// The address requests go to now.
    public var current: URL { lock.withLock { _current } }

    /// Away from home: reaching the server by its remote address.
    public var isAway: Bool { lock.withLock { _current != home } }

    /// The other address, if there is one.
    public func alternate(to url: URL) -> URL? {
        guard let remote else { return nil }
        return url == remote ? home : remote
    }

    /// `url` answered: send everything there from now on.
    public func reached(_ url: URL) {
        lock.withLock {
            if url != home, _current == home {
                homeCheckedAt = Date() // leaving home starts the clock for trying it again
            }
            _current = url
        }
    }

    /// True once per interval while away: time to see whether home answers again.
    func shouldCheckHome(now: Date = Date()) -> Bool {
        lock.withLock {
            guard _current != home else { return false }
            if let homeCheckedAt, now.timeIntervalSince(homeCheckedAt) < Self.homeCheckInterval { return false }
            homeCheckedAt = now
            return true
        }
    }

    // MARK: Sharing

    private static let registryLock = NSLock()
    nonisolated(unsafe) private static var registry: [String: ServerAddresses] = [:]

    /// The one instance for this pair of addresses, so clients made at different times share what they've learnt.
    public static func shared(home: URL, remote: URL?) -> ServerAddresses {
        let key = "\(home.absoluteString)|\(remote?.absoluteString ?? "")"
        return registryLock.withLock {
            if let existing = registry[key] { return existing }
            let made = ServerAddresses(home: home, remote: remote)
            registry[key] = made
            return made
        }
    }
}
