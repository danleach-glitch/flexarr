import FlexarrKit
import Foundation
import Observation
import UIKit

/// Connecting by code: the server hands out a short code, someone signed in to Flexarr approves it (Settings ›
/// Connect a TV, or the link), and this device gets a sign-in of its own. Codes that run out are replaced.
@MainActor
@Observable
final class Pairing {
    let server: DiscoveredServer
    private(set) var current: PairingStart?
    /// The code on screen replaced one that ran out, so anyone part-way through typing the old one notices.
    private(set) var renewedAt: Date?
    private(set) var error: String?

    init(server: DiscoveredServer) {
        self.server = server
    }

    /// The approval page for `start`, for a QR code or to open in the browser.
    func link(for start: PairingStart) -> URL {
        server.address.appending(path: "link").appending(queryItems: [URLQueryItem(name: "code", value: start.code)])
    }

    /// Gets a code, then polls until it's approved (and signs in) or expires (and gets a new one). Runs until it
    /// signs in or the calling task is cancelled.
    func run(model: AppModel) async {
        let client = FlexarrClient(baseURL: server.address)
        while !Task.isCancelled {
            do {
                let started = try await client.startPairing(deviceName: UIDevice.current.name)
                if let shown = current, shown.code != started.code { renewedAt = Date() }
                current = started
                error = nil
                while !Task.isCancelled {
                    try await Task.sleep(for: .seconds(2))
                    if let renewedAt, Date().timeIntervalSince(renewedAt) > 10 { self.renewedAt = nil }
                    let status = try await client.pairingStatus(started.id)
                    if status.status == "approved", let key = status.apiKey {
                        try await connect(with: key, model: model)
                        return
                    }
                    if status.status == "expired" { break } // a fresh code next time round
                }
            } catch is CancellationError {
                return
            } catch {
                self.error = ConnectionError(error).message
                try? await Task.sleep(for: .seconds(5))
            }
        }
    }

    /// Signs in with the token the server just handed over. It's only handed over once, so a network blip here
    /// is retried a few times rather than starting over with a new code (and leaving a sign-in nobody holds).
    private func connect(with token: String, model: AppModel) async throws {
        var attempt = 0
        while true {
            do {
                try await model.connect(address: server.address.absoluteString, apiKey: token)
                return
            } catch {
                // Back was pressed (a cancelled request can also fail as a URL error).
                if error is CancellationError || Task.isCancelled {
                    abandon(token)
                    throw CancellationError()
                }
                attempt += 1
                guard attempt < 5 else {
                    // Give up on this one: sign it out (best effort) so it doesn't linger in the list.
                    abandon(token)
                    throw error
                }
                self.error = "Connecting… (\(ConnectionError(error).message))"
                do {
                    try await Task.sleep(for: .seconds(2 * attempt))
                } catch {
                    abandon(token)
                    throw error
                }
            }
        }
    }

    /// The approved token wasn't kept, so sign it out (best effort) rather than leave it in the list with nobody
    /// holding it. Detached: this task may be cancelled (Back) at any moment, which would cancel the request too.
    private func abandon(_ token: String) {
        let client = FlexarrClient(baseURL: server.address, apiKey: token)
        Task.detached { try? await client.signOut() }
    }
}
