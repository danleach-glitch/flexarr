import FlexarrKit
import FlexarrTunnel
import Foundation
import Security

private let log = FlexarrLog("tunnel")

/// This app is a family guest's (signed in through the tunnel): AirPlay is left out, since an AirPlay receiver
/// can't reach a tunnel inside this device. Set as the sign-in changes (`AppModel.phase`).
enum GuestMode {
    nonisolated(unsafe) static var isActive = false
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
        case failed(String)

        var errorDescription: String? {
            switch self {
            case .needsInvite: "You no longer have access to this Flexarr."
            case .failed(let why): "Couldn’t connect (\(why))."
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

    /// The server's address through the tunnel, once it's running.
    var address: URL? {
        lock.withLock { running.flatMap { URL(string: "http://127.0.0.1:\($0.port)") } }
    }

    /// Starts the tunnel to `info.target` (or finds it running) and returns the server's address through it. Takes
    /// a moment the first time; `authKey` is needed only then, to join. Runs off the main thread: the Go side blocks
    /// for up to a minute.
    func start(_ info: GuestTunnelInfo, authKey: String? = nil) async throws -> URL {
        // Launch, loading the profiles and coming to the front all ask at once: they share one start, since a second
        // start while the first is connecting would stop it, and two nodes can't share the state folder.
        let task: Task<URL, Error> = lock.withLock {
            if let starting, startingTarget == info.target { return starting }
            if let running, running.target == info.target, status()?.state == "running",
               let url = URL(string: "http://127.0.0.1:\(running.port)") {
                return Task { url }
            }
            let made = Task.detached(priority: .userInitiated) { [self] in try startBlocking(info, authKey: authKey) }
            starting = made
            startingTarget = info.target
            return made
        }
        defer {
            lock.withLock {
                if startingTarget == info.target { starting = nil; startingTarget = nil }
            }
        }
        return try await task.value
    }

    private var starting: Task<URL, Error>?
    private var startingTarget: String?

    private func startBlocking(_ info: GuestTunnelInfo, authKey: String?) throws -> URL {
        let folder = Self.stateFolder
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        Self.restoreIdentity(into: folder)
        FlexarrTunnelSetPreferredPort(Int32(Self.preferredPort))
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
            if status()?.state == "needs-login" { throw Failure.needsInvite }
            throw Failure.failed(why)
        }
        Self.saveIdentity(from: folder)
        lock.withLock { running = (info.target, Int(port)) }
        return URL(string: "http://127.0.0.1:\(port)")!
    }

    /// How it's going: running, and whether directly or through a relay.
    func status() -> Status? {
        guard let text = Self.take(FlexarrTunnelStatus()) else { return nil }
        return try? JSONDecoder().decode(Status.self, from: Data(text.utf8))
    }

    /// Stops the tunnel. `forget` also deletes this device's identity on the host's tailnet (leaving it).
    func stop(forget: Bool = false) {
        FlexarrTunnelStop()
        lock.withLock { running = nil }
        if forget {
            try? FileManager.default.removeItem(at: Self.stateFolder)
            Self.deleteIdentity()
        }
    }

    // MARK: Where it lives

    private static var stateFolder: URL {
        #if os(tvOS)
        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        #else
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        #endif
        var folder = base.appendingPathComponent("Tailscale", isDirectory: true)
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? folder.setResourceValues(values)
        return folder
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
    private static var keychainQuery: [String: Any] { [kSecClass as String: kSecClassGenericPassword,
                                                        kSecAttrService as String: "io.github.flexarr.tunnel",
                                                        kSecAttrAccount as String: identityFile] }

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
