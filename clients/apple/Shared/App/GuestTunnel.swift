import FlexarrKit
import FlexarrTunnel
import Foundation
import Network
import Security

private let log = FlexarrLog("tunnel")

/// Requests go through the tunnel now (always for a family guest; for a household device, while home doesn't answer):
/// AirPlay is left out, since an AirPlay receiver can't reach a tunnel inside this device, and downloads carry on only
/// while the app is open. Set as the sign-in and its route change (`AppModel.phase`, `AppModel.routeChanged`); what's
/// already open (a player, a transfer) keeps what it started with. A guest's limits are `isGuest`.
enum GuestMode {
    nonisolated(unsafe) static var isActive = false
    /// Signed in as a family guest (`ServerCredentials.isGuest`), wherever its requests go.
    nonisolated(unsafe) static var isGuest = false
}

/// What else holds a household device's tunnel open besides the players `AppModel` has: the iPhone's transfers that
/// went through it (`DownloadStore`). Each says, when asked, whether it has something under way there; the tunnel
/// stops at home only once nothing does (`AppModel.stopTunnelWhenIdle`).
@MainActor
enum TunnelUse {
    private static var holders: [@MainActor () -> Bool] = []

    static func register(_ holds: @escaping @MainActor () -> Bool) { holders.append(holds) }

    static var inUse: Bool { holders.contains { $0() } }
}

/// The tunnel a family guest's app reaches its host's server through: Tailscale built in (clients/tunnel), joined to
/// the host's tailnet with the invite's one-use key, and opened here as a port on 127.0.0.1 that every request,
/// artwork fetch and player uses as the server's address. See docs/remote-guests.md.
///
/// The node's identity is a small file in its state folder, kept so it never needs a key again: in Application
/// Support on iPhone (not backed up), and on Apple TV, which keeps no files for good, a copy in the Keychain that's
/// put back whenever the system has cleared the folder.
final class GuestTunnel: @unchecked Sendable {
    static let shared = GuestTunnel()

    enum Failure: LocalizedError {
        /// The tailnet wants a key again: this device was deleted from the host's tailnet, or its key ran out.
        case needsInvite
        /// The host's tailnet wasn't reached within a minute: no internet here, or the host's server is off.
        case timedOut
        case failed(String)

        var errorDescription: String? {
            switch self {
            case .needsInvite: t("You no longer have access to this Flexarr.")
            case .timedOut: t("Couldn’t reach the server in time.")
            case .failed(let why): t("Couldn’t connect ({reason}).", ["reason": why])
            }
        }
    }

    struct Status: Decodable {
        let state: String
        let path: String?
        let error: String?
    }

    private let lock = NSLock()
    private var running: (target: String, port: Int)?
    /// Goes up with each stop: a start begun before it ends stopped, rather than left running (see `stop`).
    private var stops = 0

    /// The server's address through the tunnel, once it's running.
    var address: URL? {
        lock.withLock { running.flatMap { URL(string: "http://127.0.0.1:\($0.port)") } }
    }

    /// Starts the tunnel to `info.target` (or finds it running) and returns the server's address through it. Takes
    /// a moment the first time; `authKey` is needed only then, to join. Runs off the main thread: the Go side blocks
    /// for up to a minute.
    ///
    /// `answers`, when given, is asked whether the server answers through a tunnel that's already running; if it
    /// doesn't, the tunnel starts again. "Running" is only what the Go side last knew: while the app was suspended
    /// (the screen locked a while) the system closed the node's connections, and it doesn't notice by itself.
    func start(_ info: GuestTunnelInfo, authKey: String? = nil,
               answers: (@Sendable (URL) async -> Bool)? = nil) async throws -> URL {
        // Launch, loading the profiles and coming to the front all ask at once: they share one start, since a second
        // start while the first is connecting would stop it, and two nodes can't share the state folder.
        let task: Task<URL, Error> = lock.withLock {
            if let starting, startingTarget == info.target { return starting }
            // Cheap (never waits on the Go side), so asking each time the app comes to the front costs nothing.
            if let running, running.target == info.target, Self.state() == "running",
               let url = URL(string: "http://127.0.0.1:\(running.port)") {
                guard let answers else { return Task { url } }
                // Shared like a start, so the checks that come with coming to the front restart it only once.
                let stopsBefore = stops
                let made = Task.detached(priority: .userInitiated) { [self] in
                    if await answers(url) { return url }
                    log.info("The server stopped answering through the tunnel: starting it again")
                    lock.withLock { self.running = nil }
                    FlexarrTunnelStop()
                    // Its identity is kept, so no key is needed.
                    return try startBlocking(info, authKey: nil, stopsBefore: stopsBefore)
                }
                starting = made
                startingTarget = info.target
                return made
            }
            // After any stop still under way, so it can't stop this one.
            let stopping = self.stopping
            let stopsBefore = stops
            let made = Task.detached(priority: .userInitiated) { [self] in
                await stopping?.value
                return try startBlocking(info, authKey: authKey, stopsBefore: stopsBefore)
            }
            starting = made
            startingTarget = info.target
            return made
        }
        defer {
            lock.withLock {
                if starting == task { starting = nil; startingTarget = nil }
            }
        }
        return try await task.value
    }

    private var starting: Task<URL, Error>?
    private var startingTarget: String?

    /// `stopsBefore`: how many stops there had been when it was asked for. One asked for since ends it as it comes up
    /// (the stop waits for it, then stops the node), and it throws `CancellationError`.
    private func startBlocking(_ info: GuestTunnelInfo, authKey: String?, stopsBefore: Int) throws -> URL {
        var folder = Self.stateFolder
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        // Once the folder is there (the first time, it isn't until now).
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? folder.setResourceValues(values)
        Self.restoreIdentity(into: folder)
        FlexarrTunnelSetPreferredPort(Int32(Self.preferredPort))
        // Only this app's requests (which carry this launch's token) get through, not another app's on this device.
        GuestTunnelToken.current.withCString { FlexarrTunnelSetClientToken(UnsafeMutablePointer(mutating: $0)) }
        let port = folder.path.withCString { dir in
            info.hostname.withCString { host in
                (authKey ?? "").withCString { key in
                    info.target.withCString { target in
                        FlexarrTunnelStart(UnsafeMutablePointer(mutating: dir), UnsafeMutablePointer(mutating: host),
                                           UnsafeMutablePointer(mutating: key), UnsafeMutablePointer(mutating: target), nil)
                    }
                }
            }
        }
        guard port > 0 else {
            let why = Self.take(FlexarrTunnelLastError()) ?? "unknown error"
            log.warning("The tunnel didn't start: \(why)")
            // Tailscale turns a used or cancelled key away as "invalid key".
            if Self.state() == "needs-login" || why.localizedCaseInsensitiveContains("invalid key") { throw Failure.needsInvite }
            if why.localizedCaseInsensitiveContains("timed out") || why.localizedCaseInsensitiveContains("deadline exceeded") {
                throw Failure.timedOut
            }
            throw Failure.failed(why)
        }
        Self.saveIdentity(from: folder)
        // Its key is spent now: trying the same invite again (a sign-in that didn't go through) uses this identity.
        if authKey?.isEmpty == false { UserDefaults.standard.set(Self.identity(info), forKey: Self.identityKey) }
        let stopped = lock.withLock {
            guard stops == stopsBefore else { return true }
            running = (info.target, Int(port))
            return false
        }
        if stopped {
            log.info("The tunnel came up after it was stopped: stopping it")
            throw CancellationError()
        }
        GuestTunnelToken.tunnel(listeningOn: Int(port))
        return URL(string: "http://127.0.0.1:\(port)")!
    }

    /// This device already joined `info`'s tailnet with an invite's key (spent now), and still has that identity.
    static func hasIdentity(for info: GuestTunnelInfo) -> Bool {
        UserDefaults.standard.string(forKey: identityKey) == identity(info)
    }

    private static let identityKey = "FlexarrTunnelIdentity"
    private static func identity(_ info: GuestTunnelInfo) -> String { "\(info.target)|\(info.hostname)" }

    /// How it's going: running, and whether directly or through a relay. Asks the Go side, which can take a moment:
    /// call it off the main thread (`connectionPath`).
    func status() -> Status? {
        guard let text = Self.take(FlexarrTunnelStatus()) else { return nil }
        return try? JSONDecoder().decode(Status.self, from: Data(text.utf8))
    }

    /// How the tunnel reaches the host now ("direct", "peer-relay", "derp"), asked off the main thread.
    func connectionPath() async -> String? {
        await Task.detached(priority: .utility) { [self] in status()?.path }.value
    }

    /// "running", "starting", "stopped", "needs-login" or "error": never waits.
    static func state() -> String? {
        take(FlexarrTunnelState())
    }

    private var stopping: Task<Void, Never>?

    /// Stops the tunnel, off the main thread (the Go side waits for the node to close). `forget` also deletes this
    /// device's identity on the host's tailnet (leaving it). A start under way is waited for and then stopped too (it
    /// throws `CancellationError` to whoever waits on it), so nothing comes up after this. A start made after this
    /// waits for it; await the task returned to know it's done.
    @discardableResult
    func stop(forget: Bool = false) -> Task<Void, Never> {
        GuestTunnelToken.tunnel(listeningOn: nil)
        return lock.withLock {
            running = nil
            stops += 1
            let underWay = starting
            starting = nil
            startingTarget = nil
            let previous = stopping
            let task = Task.detached(priority: .userInitiated) {
                await previous?.value
                _ = try? await underWay?.value
                FlexarrTunnelStop()
                if forget {
                    try? FileManager.default.removeItem(at: Self.stateFolder)
                    Self.deleteIdentity()
                    UserDefaults.standard.removeObject(forKey: Self.identityKey)
                }
            }
            stopping = task
            return task
        }
    }

    // MARK: Where it lives

    private static var stateFolder: URL {
        #if os(tvOS)
        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        #else
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        #endif
        return base.appendingPathComponent("Tailscale", isDirectory: true)
    }

    /// The same loopback port every launch, chosen once, so addresses kept from the last launch (artwork in the
    /// image cache) still match. A busy port falls back to any free one.
    private static var preferredPort: Int {
        let key = "FlexarrTunnelPort"
        let saved = UserDefaults.standard.integer(forKey: key)
        if saved > 0 { return saved }
        let chosen = Int.random(in: 49_152...64_999)
        UserDefaults.standard.set(chosen, forKey: key)
        return chosen
    }

    private static func take(_ pointer: UnsafeMutablePointer<CChar>?) -> String? {
        guard let pointer else { return nil }
        defer { FlexarrTunnelFree(pointer) }
        return String(cString: pointer)
    }

    // MARK: The identity's copy in the Keychain

    private static let identityFile = "tailscaled.state"
    private static var keychainQuery: [String: Any] { KeychainPlacement.placed([kSecClass as String: kSecClassGenericPassword,
                                                                                kSecAttrService as String: "io.github.flexarr.tunnel",
                                                                                kSecAttrAccount as String: identityFile]) }

    private static func restoreIdentity(into folder: URL) {
        let file = folder.appendingPathComponent(identityFile)
        guard !FileManager.default.fileExists(atPath: file.path) else { return }
        var query = keychainQuery
        query[kSecReturnData as String] = true
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return }
        try? data.write(to: file, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
        log.info("Put the tunnel's identity back from the Keychain")
    }

    private static func saveIdentity(from folder: URL) {
        guard let data = try? Data(contentsOf: folder.appendingPathComponent(identityFile)) else { return }
        let attributes: [String: Any] = [kSecValueData as String: data, kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly]
        if SecItemUpdate(keychainQuery as CFDictionary, attributes as CFDictionary) == errSecItemNotFound {
            SecItemAdd(keychainQuery.merging(attributes) { _, new in new } as CFDictionary, nil)
        }
    }

    private static func deleteIdentity() {
        SecItemDelete(keychainQuery as CFDictionary)
    }
}
