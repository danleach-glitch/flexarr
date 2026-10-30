import FlexarrKit
import Foundation

private let log = FlexarrLog("commands")

/// Keeps the server's command stream open while the app is in front, so another device (an iPhone saying "play the
/// next episode on the living room TV" to Siri), the web app or an assistant can play, pause, resume and stop here.
/// Signed in as the main profile, it also hears about devices asking to connect (the iPhone asks to allow them).
/// Reconnects when the connection drops, backing off; stops for good when the server says this sign-in has no stream
/// (an app holding the API key itself rather than a token of its own).
@MainActor
final class DeviceCommandListener {
    private var task: Task<Void, Never>?
    private var listeningFor: FlexarrClient?

    /// The commands beyond the basic ones this app takes: a remote on another device offers these.
    static let accepts: [DeviceCommandKind] = [.seek, .skip, .next, .previous, .skipSegment, .audioTrack, .subtitleTrack, .rate]

    /// Listens on `client`'s sign-in, handing each command (and connect prompt, and with `nowPlaying` what every device
    /// is playing as it changes) to `handle`. Called again with the same client, it carries on.
    func start(_ client: FlexarrClient, nowPlaying: Bool = false, handle: @escaping @MainActor (DeviceEvent) -> Void) {
        if let listeningFor, listeningFor.baseURL == client.baseURL, listeningFor.apiKey == client.apiKey, task?.isCancelled == false { return }
        stop()
        listeningFor = client
        task = Task { [weak self] in
            var delay: Duration = .seconds(2)
            while !Task.isCancelled {
                do {
                    for try await event in client.deviceEvents(accepts: Self.accepts, nowPlaying: nowPlaying) {
                        switch event {
                        case .command(let command): log.info("Command \(command.kind.rawValue) from \(command.sentBy ?? "someone")")
                        case .pairing(let request): log.info("\(request.deviceName) asks to connect")
                        case .pairingEnded, .nowPlaying: break
                        }
                        handle(event)
                        delay = .seconds(2)
                    }
                    // Ended cleanly: a newer connection from this device took over (the app was opened twice, say).
                    log.info("Command stream ended")
                } catch FlexarrClientError.http(status: 403, _) {
                    log.info("This sign-in has no command stream; not listening")
                    self?.listeningFor = nil
                    return
                } catch is CancellationError {
                    return
                } catch {
                    if Task.isCancelled { return }
                    log.info("Command stream dropped; retrying in \(delay.components.seconds)s", error: error)
                }
                try? await Task.sleep(for: delay)
                delay = min(delay * 2, .seconds(60))
            }
        }
    }

    func stop() {
        task?.cancel()
        task = nil
        listeningFor = nil
    }
}
