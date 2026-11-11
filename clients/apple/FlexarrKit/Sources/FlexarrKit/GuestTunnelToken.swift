import Foundation

/// The secret a family guest's tunnel (clients/tunnel) asks of every connection to its port on 127.0.0.1, so another
/// app on the same device can't reach the host's server through it. New each launch and never stored: the app hands
/// it to the tunnel before it starts (`FlexarrTunnelSetClientToken`), then names the tunnel's address here once it's
/// up, and every request to that address carries it in `header`. Requests anywhere else never do.
public enum GuestTunnelToken {
    /// The header the token goes in (the tunnel's `TokenHeader`).
    public static let header = "X-Flexarr-Tunnel"

    /// This launch's token: 32 random bytes, as hex.
    public static let current: String = {
        var generator = SystemRandomNumberGenerator()
        return (0..<32).map { _ in String(format: "%02x", UInt8.random(in: .min ... .max, using: &generator)) }.joined()
    }()

    private static let lock = NSLock()
    nonisolated(unsafe) private static var port: Int?

    /// The tunnel is listening on `port` of 127.0.0.1 (nil: it isn't running).
    public static func tunnel(listeningOn port: Int?) {
        lock.withLock { self.port = port }
    }

    /// The headers a request to `url` needs to pass the tunnel: the token when `url` is the tunnel's address, else none.
    public static func headers(for url: URL?) -> [String: String] {
        guard let url, url.host == "127.0.0.1", let port = url.port, lock.withLock({ self.port == port }) else { return [:] }
        return [header: current]
    }

    /// `request` with the token added when it goes through the tunnel.
    public static func applied(to request: URLRequest) -> URLRequest {
        var request = request
        for (name, value) in headers(for: request.url) { request.setValue(value, forHTTPHeaderField: name) }
        return request
    }

    /// A request for `url`, carrying the token when it goes through the tunnel.
    public static func request(_ url: URL) -> URLRequest {
        applied(to: URLRequest(url: url))
    }
}
