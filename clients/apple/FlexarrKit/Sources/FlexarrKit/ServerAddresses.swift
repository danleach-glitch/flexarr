import Foundation

/// Where a server is reached: the address it was connected with (usually on the home network) and, once the server
/// has remote access turned on, its address for away from home (over Tailscale). Requests go to whichever answered
/// last. A GET that can't connect there, or hasn't heard back after a moment, is also sent to the other address and
/// the first answer wins; a write only moves over once the first address has failed to connect, so it's never sent
/// twice. While away, home is tried again every few minutes so the app goes back to it once it's reachable.
///
/// The home address is a private one over plain HTTP, and on someone else's network (a café, a hotel) something else
/// may answer at it. So once the app knows the server's id (`serverId`), home is asked who it is, without the sign-in,
/// before it's sent anything signed in (`proveHome`): after a launch, and again whenever it hasn't answered for
/// `proofLasts`, which covers the device having moved to another network. Until home gives the same id it counts as
/// not there. An https home says who it is by its certificate and isn't asked.
///
/// Shared by every client made for the same sign-in (see `shared(home:remote:)`), so what one request learns, the
/// next one uses.
public final class ServerAddresses: @unchecked Sendable {
    public let home: URL
    public let remote: URL?
    /// The id the server at `home` must give before it's sent the sign-in (`SystemStatus.serverId`); nil while the
    /// app doesn't know it (a server before 0.55, a guest's own tunnel), and home is then trusted as it always was.
    public let serverId: String?

    /// How long to wait for the current address before also asking the other one (GETs only).
    public static let alternateDelay: Duration = .milliseconds(2500)
    /// How long the reachability check gives the address in use before asking the other one too.
    public static let probeHeadStart: Duration = .milliseconds(1500)
    /// While away, how often home is tried again.
    public static let homeCheckInterval: TimeInterval = 5 * 60
    /// How long home stays trusted after it last said who it is or answered a request: a device can't have gone to
    /// another network and found something at the same address in less.
    public static let proofLasts: TimeInterval = 60

    private let lock = NSLock()
    private var _current: URL
    private var homeCheckedAt: Date?
    private var homeProvenAt: Date?
    private var proofUnderWay = false
    private var proofWaiters: [AsyncStream<Bool>.Continuation] = []

    public init(home: URL, remote: URL?, serverId: String? = nil) {
        self.home = home
        self.remote = remote == home ? nil : remote
        self.serverId = serverId?.isEmpty == false ? serverId : nil
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
                homeProvenAt = nil // and whatever is at its address next has to say who it is
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

    // MARK: Home says who it is

    /// Whether `url` is asked for the server's id before it's sent the sign-in: the home address, over plain HTTP,
    /// once the id is known.
    public func asksProof(of url: URL) -> Bool {
        url == home && serverId != nil && home.scheme?.lowercased() != "https"
    }

    /// Whether `url` must say who it is before the next signed-in request goes to it.
    func needsProof(_ url: URL, now: Date = Date()) -> Bool {
        guard asksProof(of: url) else { return false }
        return lock.withLock { !isProven(now) }
    }

    private func isProven(_ now: Date) -> Bool {
        guard let homeProvenAt else { return false }
        return now.timeIntervalSince(homeProvenAt) < Self.proofLasts
    }

    /// `url` answered a signed-in request: home, if it had said who it is, stays trusted from now.
    func answered(from url: URL, now: Date = Date()) {
        guard url == home else { return }
        lock.withLock { if isProven(now) { homeProvenAt = now } }
    }

    /// Whether home may be sent the sign-in: true at once when it needn't say who it is, or has lately. Otherwise
    /// `ask` puts the question (home's address and the id it should give; true when it gave that id), once however
    /// many requests are waiting on the answer. `fresh` asks even if it said so lately (is it there now?). False when
    /// home didn't answer, something else did, or the task waiting was cancelled (the question carries on for the
    /// others).
    func proveHome(fresh: Bool = false, now: Date = Date(),
                   by ask: @escaping @Sendable (_ home: URL, _ id: String) async -> Bool) async -> Bool {
        guard let serverId, asksProof(of: home) else { return true }
        let (answers, waiter) = AsyncStream<Bool>.makeStream()
        enum Step { case proven, wait, ask }
        let step: Step = lock.withLock {
            if !fresh, isProven(now) { return .proven }
            proofWaiters.append(waiter)
            if proofUnderWay { return .wait }
            proofUnderWay = true
            return .ask
        }
        switch step {
        case .proven:
            return true
        case .ask:
            let home = home
            // On its own, so one request being cancelled doesn't take the answer from the rest.
            Task.detached {
                let same = await ask(home, serverId)
                self.proofEnded(same)
            }
        case .wait:
            break
        }
        for await same in answers { return same }
        return false
    }

    private func proofEnded(_ same: Bool) {
        let waiting = lock.withLock {
            proofUnderWay = false
            homeProvenAt = same ? Date() : nil
            let waiting = proofWaiters
            proofWaiters = []
            return waiting
        }
        for waiter in waiting {
            waiter.yield(same)
            waiter.finish()
        }
    }

    // MARK: Sharing

    private static let registryLock = NSLock()
    nonisolated(unsafe) private static var registry: [String: ServerAddresses] = [:]

    /// The one instance for this pair of addresses (and the server expected there), so clients made at different
    /// times share what they've learnt.
    public static func shared(home: URL, remote: URL?, serverId: String? = nil) -> ServerAddresses {
        let key = "\(home.absoluteString)|\(remote?.absoluteString ?? "")|\(serverId ?? "")"
        return registryLock.withLock {
            if let existing = registry[key] { return existing }
            let made = ServerAddresses(home: home, remote: remote, serverId: serverId)
            registry[key] = made
            return made
        }
    }
}
