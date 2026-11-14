import Foundation

/// Which address for away from home an app keeps, so its sign-in only ever goes to its own server.
extension FlexarrClient {
    /// Whether `url`, which a server gave as its address for away from home, is one the app may send this sign-in
    /// to: https on a Tailscale name (`*.ts.net`, what the server's remote access gives), or a Tailscale address
    /// (100.64.0.0/10). The server says it without proving it's the same server, so an answer tampered with on the
    /// home network can't point the app at a host on the internet that would collect the sign-in from then on.
    public static func isAcceptableRemote(_ url: URL) -> Bool {
        guard var host = url.host()?.lowercased() else { return false }
        while host.hasSuffix(".") { host.removeLast() }
        if url.scheme?.lowercased() == "https", host.hasSuffix(".ts.net") { return true }
        return isTailscaleAddress(host)
    }

    /// `host` is an IPv4 address in Tailscale's range, 100.64.0.0/10. A name is never looked up.
    static func isTailscaleAddress(_ host: String) -> Bool {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return false }
        let octets = parts.compactMap { !$0.isEmpty && $0.count <= 3 && $0.allSatisfy { $0.isASCII && $0.isNumber } ? UInt8($0) : nil }
        guard octets.count == 4 else { return false }
        return octets[0] == 100 && octets[1] & 0xC0 == 0x40
    }

    /// Which server answers at `url`, asked without any sign-in (GET /setup, giving up after 5 seconds): its id
    /// (`SystemStatus.serverId`), or nil when it gave none (a server before 0.55, or not a Flexarr server at all).
    /// Throws `FlexarrClientError.unreachable` when nothing answered there, which says nothing either way.
    public static func serverId(at url: URL, session: URLSession = .shared) async throws -> String? {
        let client = FlexarrClient(baseURL: url, session: session)
        do {
            let data = try await client.send("GET", "setup", timeout: 5)
            return try client.decode(SetupStatus.self, from: data).serverId
        } catch let error where couldNotConnect(error) || error is CancellationError || (error as? URLError)?.code == .cancelled {
            throw FlexarrClientError.unreachable
        } catch {
            return nil
        }
    }

    /// The server's address for away from home, as the app keeps it: the address, and whether it has given the
    /// server's id to this device yet. One that hasn't (`confirmed` false) is asked before it's sent the sign-in (see
    /// `ServerAddresses.remoteConfirmed`).
    public struct Away: Sendable, Equatable {
        public var url: URL
        public var confirmed: Bool

        public init(_ url: URL, confirmed: Bool = true) {
            self.url = url
            self.confirmed = confirmed
        }
    }

    /// The server's address for away from home, from its `status`, as a base URL: every request there carries the
    /// sign-in. Nil when it has none, it's the `address` already in use, it isn't a Tailscale one (see
    /// `isAcceptableRemote`), or a different server answers there. A new one is taken once the server there gives
    /// this server's id (asked by `idAt` without the sign-in). While nothing answers there (Tailscale isn't connected
    /// on this device just now), an address confirmed before (`kept`) stays; without one, the new address is kept
    /// unconfirmed, to be asked before the first signed-in request goes to it, so a device that signed in at home
    /// with Tailscale off still finds the server once it's away with Tailscale on.
    ///
    /// The id it has to give is `knownId`, the one this device kept when it signed in (`ServerCredentials.serverId`),
    /// whatever `status` says: a status tampered with on the home network can name a host of its own on Tailscale
    /// and an id to match, but not one that gives the id kept from before. Only without one (signing in, or a sign-in
    /// from before the app kept it) is the id in `status` what's asked for. Servers before 0.55 have no id, so their
    /// address is taken without asking.
    public static func remoteAddress(
        for status: SystemStatus,
        address: URL,
        kept: Away? = nil,
        knownId: String? = nil,
        idAt: @Sendable (URL) async throws -> String? = { try await serverId(at: $0) },
        refused: @Sendable (String) -> Void
    ) async -> Away? {
        guard let given = status.remoteUrl?.absoluteString,
              let url = normalizeAddress(given.removingPercentEncoding ?? given) else { return nil }
        guard isAcceptableRemote(url) else {
            refused("The server's address for away from home isn't a Tailscale one; not using it")
            return nil
        }
        guard url != address else { return nil }
        // Checked when it was kept.
        if let kept, kept.url == url, kept.confirmed { return kept }
        guard let id = knownId?.isEmpty == false ? knownId : status.serverId else { return Away(url) }
        let there: String?
        do {
            there = try await idAt(url)
        } catch {
            if let kept, kept.confirmed { return kept }
            return Away(url, confirmed: false)
        }
        guard there == id else {
            refused("A different server answers at the server's address for away from home; not using it")
            return nil
        }
        return Away(url)
    }
}
