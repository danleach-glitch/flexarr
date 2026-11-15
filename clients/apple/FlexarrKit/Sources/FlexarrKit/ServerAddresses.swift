import Foundation

/// Where a server is reached: the address it was connected with (usually on the home network) and, once the server
/// has remote access turned on, its address for away from home (over Tailscale). Requests go to whichever answered
/// last. A GET that can't connect there, or hasn't heard back after a moment, is also sent to the other address and
/// the first answer wins; a write only moves over once the first address has failed to connect, so it's never sent
/// twice. While away, home is tried again every few minutes so the app goes back to it once it's reachable.
///
/// Which address answered last is kept between launches (`shared` instances only), so an app started away from home
/// begins at the remote address rather than waiting on home first; home is then checked as soon as something has
/// answered, so an app that has come home goes back to it.
///
/// The home address is a private one over plain HTTP, and on someone else's network (a café, a hotel) something else
/// may answer at it. So once the app knows the server's id (`serverId`), home is asked who it is, without the sign-in,
/// before it's sent anything signed in (`proveHome`): after a launch, and again whenever it hasn't answered for
/// `proofLasts`, which covers the device having moved to another network. Until home gives the same id it counts as
/// not there. An https home says who it is by its certificate and isn't asked.
///
/// The away address is asked the same once, when it's one the app kept without hearing from it (`remoteConfirmed`
/// false: the device couldn't reach it over Tailscale when the server gave it). Until it gives the server's id, it
/// counts as not there either; once it has, it stays trusted for this launch. The id isn't secret (anyone may ask
/// for it), so this catches a different server at the address (a Tailscale address handed to another machine, a
/// name that now leads elsewhere), not one set up to pretend.
///
/// Shared by every client made for the same sign-in (see `shared(home:remote:)`), so what one request learns, the
/// next one uses.
public final class ServerAddresses: @unchecked Sendable {
    public let home: URL
    public let remote: URL?
    /// The id the server at `home` must give before it's sent the sign-in (`SystemStatus.serverId`); nil while the
    /// app doesn't know it (a server before 0.55, a guest's own tunnel), and home is then trusted as it always was.
    public let serverId: String?
    /// False while `remote` hasn't yet given the server's id to this device: it's asked before it's sent the sign-in.
    /// Only ever goes from false to true (see `shared`).
    public var remoteConfirmed: Bool { lock.withLock { _remoteConfirmed } }
    private var _remoteConfirmed: Bool

    /// How long to wait for the current address before also asking the other one (GETs only).
    public static let alternateDelay: Duration = .milliseconds(2500)
    /// How long the reachability check gives the address in use before asking the other one too.
    public static let probeHeadStart: Duration = .milliseconds(1500)
    /// While away, how often home is tried again.
    public static let homeCheckInterval: TimeInterval = 5 * 60
    /// How long home stays trusted after it last said who it is or answered a request: a device can't have gone to
    /// another network and found something at the same address in less.
    public static let proofLasts: TimeInterval = 60
    /// How long an away address that didn't give the server's id counts as not there before it's asked again, so
    /// while Tailscale is off each request isn't held up asking it afresh.
    public static let remoteMissLasts: TimeInterval = 30
    /// Posted (object: the `ServerAddresses`) when the away address first gives the server's id.
    public static let remoteProvenNotification = Notification.Name("io.github.flexarr.server.remoteProven")

    private let lock = NSLock()
    private var _current: URL
    private var homeCheckedAt: Date?
    private var homeProvenAt: Date?
    private var remoteProven = false
    /// When a plain-http away address last said who it is or answered (see `asksProof`).
    private var remoteProvenAt: Date?
    private var remoteMissedAt: Date?
    /// Who's waiting on each address's answer while one is being asked.
    private var proofWaiters: [URL: [AsyncStream<Bool>.Continuation]] = [:]
    /// Where the address that answered last is kept between launches; nil keeps nothing.
    private let memory: UserDefaults?
    static let memoryKey = "io.github.flexarr.server.lastReached"

    /// `memory`: where the address that answered last is kept, to start there next launch (nil: always starts at home).
    public init(home: URL, remote: URL?, serverId: String? = nil, remoteConfirmed: Bool = true, memory: UserDefaults? = nil) {
        self.home = home
        self.remote = remote == home ? nil : remote
        self.serverId = serverId?.isEmpty == false ? serverId : nil
        self._remoteConfirmed = remoteConfirmed
        self.memory = memory
        // The same server at either address: its artwork is kept once, under its home address (see `atHome`).
        if let remote = self.remote { Self.pair(remote: remote, home: home) }
        // Away last time: most likely still away, and a launch needn't wait on home to find that out again.
        if let remote = self.remote, memory?.string(forKey: Self.memoryKey) == remote.absoluteString {
            _current = remote
        } else {
            _current = home
        }
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
        let moved = lock.withLock {
            if url != home, _current == home {
                homeCheckedAt = Date() // leaving home starts the clock for trying it again
                homeProvenAt = nil // and whatever is at its address next has to say who it is
            } else if url == home, _current != home {
                remoteProvenAt = nil // the same for a plain-http away address, once home again
            }
            let moved = _current != url
            _current = url
            return moved
        }
        if moved { memory?.set(url.absoluteString, forKey: Self.memoryKey) }
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

    // MARK: An address says who it is

    /// Whether `url` is asked for the server's id before it's sent the sign-in, once the id is known: the home
    /// address over plain HTTP, an away address not yet confirmed (see `remoteConfirmed`), and one over plain HTTP (a
    /// 100.x Tailscale address, a range some mobile networks also use for machines of their own), which is then
    /// asked as home is, whenever it hasn't answered for `proofLasts`.
    public func asksProof(of url: URL) -> Bool {
        guard serverId != nil else { return false }
        if url == home { return home.scheme?.lowercased() != "https" }
        return url == remote && (!remoteConfirmed || !remoteIsHttps)
    }

    private var remoteIsHttps: Bool { remote?.scheme?.lowercased() == "https" }

    /// Whether `url` must say who it is before the next signed-in request goes to it.
    func needsProof(_ url: URL, now: Date = Date()) -> Bool {
        guard asksProof(of: url) else { return false }
        return lock.withLock { !isProven(url, now) }
    }

    /// The away address has given the server's id since this was made (it needn't be asked again).
    public var remoteHasProven: Bool { lock.withLock { remoteProven } }

    private func isProven(_ url: URL, _ now: Date) -> Bool {
        if url != home, remoteIsHttps { return remoteProven }
        guard let at = url == home ? homeProvenAt : remoteProvenAt else { return false }
        return now.timeIntervalSince(at) < Self.proofLasts
    }

    /// `url` answered a signed-in request: an address that has to say who it is from time to time, if it had, stays
    /// trusted from now.
    func answered(from url: URL, now: Date = Date()) {
        lock.withLock {
            guard isProven(url, now) else { return }
            if url == home { homeProvenAt = now } else if url == remote { remoteProvenAt = now }
        }
    }

    /// Whether home may be sent the sign-in (see `prove`).
    func proveHome(fresh: Bool = false, now: Date = Date(),
                   by ask: @escaping @Sendable (_ home: URL, _ id: String) async -> Bool) async -> Bool {
        await prove(home, fresh: fresh, now: now, by: ask)
    }

    /// Whether `url` may be sent the sign-in: true at once when it needn't say who it is, or has lately. Otherwise
    /// `ask` puts the question (the address and the id it should give; true when it gave that id), once however many
    /// requests are waiting on the answer. `fresh` asks even if it said so lately (is it there now?). False when it
    /// didn't answer, something else did, or the task waiting was cancelled (the question carries on for the others).
    func prove(_ url: URL, fresh: Bool = false, now: Date = Date(),
               by ask: @escaping @Sendable (_ url: URL, _ id: String) async -> Bool) async -> Bool {
        guard let serverId, asksProof(of: url) else { return true }
        let (answers, waiter) = AsyncStream<Bool>.makeStream()
        enum Step { case proven, missed, wait, ask }
        let step: Step = lock.withLock {
            if !fresh, isProven(url, now) { return .proven }
            // Asked lately and it didn't answer: still not there.
            if !fresh, url != home, let remoteMissedAt, now.timeIntervalSince(remoteMissedAt) < Self.remoteMissLasts {
                return .missed
            }
            let underWay = proofWaiters[url] != nil
            proofWaiters[url, default: []].append(waiter)
            return underWay ? .wait : .ask
        }
        switch step {
        case .proven:
            return true
        case .missed:
            waiter.finish()
            return false
        case .ask:
            // On its own, so one request being cancelled doesn't take the answer from the rest.
            Task.detached {
                let same = await ask(url, serverId)
                self.proofEnded(url, same)
            }
        case .wait:
            break
        }
        for await same in answers { return same }
        return false
    }

    private func proofEnded(_ url: URL, _ same: Bool) {
        let (waiting, firstProof) = lock.withLock {
            var first = false
            if url == home {
                homeProvenAt = same ? Date() : nil
            } else if same {
                first = !remoteProven
                remoteProven = true
                remoteProvenAt = Date()
                remoteMissedAt = nil
            } else {
                remoteProvenAt = nil
                remoteMissedAt = Date()
            }
            return (proofWaiters.removeValue(forKey: url) ?? [], first)
        }
        if firstProof { NotificationCenter.default.post(name: Self.remoteProvenNotification, object: self) }
        for waiter in waiting {
            waiter.yield(same)
            waiter.finish()
        }
    }

    // MARK: Sharing

    private static let registryLock = NSLock()
    nonisolated(unsafe) private static var registry: [String: ServerAddresses] = [:]

    /// The one instance for this pair of addresses (and the server expected there), so clients made at different
    /// times share what they've learnt. The away address being confirmed carries over to the one instance rather than
    /// making another, so what it knows of home isn't lost; it's never taken back, as the same away address of the same
    /// server can't stop being that server's.
    public static func shared(home: URL, remote: URL?, serverId: String? = nil, remoteConfirmed: Bool = true) -> ServerAddresses {
        let key = "\(home.absoluteString)|\(remote?.absoluteString ?? "")|\(serverId ?? "")"
        return registryLock.withLock {
            if let existing = registry[key] {
                if remoteConfirmed { existing.lock.withLock { existing._remoteConfirmed = true } }
                return existing
            }
            let made = ServerAddresses(home: home, remote: remote, serverId: serverId, remoteConfirmed: remoteConfirmed,
                                       memory: .standard)
            registry[key] = made
            return made
        }
    }
}
