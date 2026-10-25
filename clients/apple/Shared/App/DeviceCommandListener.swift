import FlexarrKit
import Foundation
import os

private let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "io.github.flexarr", category: "commands")

/// Keeps the server's command stream open while the app is in front, so another device (an iPhone saying "play the
/// next episode on the living room TV" to Siri), the web app or an assistant can play, pause, resume and stop here.
/// Reconnects when the connection drops, backing off; stops for good when the server says this sign-in has no stream
/// (an app holding the API key itself rather than a token of its own).
@MainActor
final class DeviceCommandListener {
    private var task: Task<Void, Never>?
    private var listeningFor: FlexarrClient?

    /// Listens on `client`'s sign-in, handing each command to `handle`. Called again with the same client, it carries on.
    func start(_ client: FlexarrClient, handle: @escaping @MainActor (DeviceCommand) -> Void) {
        if let listeningFor, listeningFor.baseURL == client.baseURL, listeningFor.apiKey == client.apiKey, task?.isCancelled == false { return }
        stop()
        listeningFor = client
        task = Task { [weak self] in
            var delay: Duration = .seconds(2)
            while !Task.isCancelled {
                do {
                    for try await command in client.commandStream() {
                        log.info("Command \(command.kind.rawValue, privacy: .public) from \(command.sentBy ?? "someone", privacy: .public)")
                        handle(command)
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
                    log.info("Command stream dropped (\(error.localizedDescription, privacy: .public)); retrying in \(delay.components.seconds)s")
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
