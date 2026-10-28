import Foundation
import Network
import ServiceManagement

/// Where the server asks the app for what only the app can do (src/Flexarr.Api/Endpoints/MacAppHost.cs): a port on
/// the loopback address and a token, both passed to it at launch. Each connection sends one line, the token and a
/// command, then optionally a tab and an argument, and gets one line back; a wrong token gets an empty line.
/// - `choose [folder to start in]`: shows macOS's folder window; the chosen folder's path, or nothing if cancelled.
/// - `login-item [on|off]`: whether the app opens at login (after changing it): on, off, or approval when macOS waits
///   for the person to allow it in System Settings.
@MainActor
final class AppRequests {
    let token = UUID().uuidString
    private let folders: MediaFolders
    private var listener: NWListener?
    private var starting: CheckedContinuation<UInt16, Error>?

    init(folders: MediaFolders) {
        self.folders = folders
    }

    /// Starts listening and returns the port, once there is one.
    func start() async throws -> UInt16 {
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = NWEndpoint.hostPort(host: .ipv4(.loopback), port: .any)
        let listener = try NWListener(using: parameters)
        self.listener = listener
        // On the main queue, so its callbacks run on the main actor.
        listener.newConnectionHandler = { [weak self] connection in
            MainActor.assumeIsolated { self?.answer(connection) }
        }
        return try await withCheckedThrowingContinuation { continuation in
            starting = continuation
            listener.stateUpdateHandler = { [weak self] state in
                MainActor.assumeIsolated { self?.listenerChanged(state) }
            }
            listener.start(queue: .main)
        }
    }

    private func listenerChanged(_ state: NWListener.State) {
        guard let starting else { return }
        switch state {
        case .ready:
            self.starting = nil
            starting.resume(returning: listener?.port?.rawValue ?? 0)
        case .failed(let error):
            self.starting = nil
            starting.resume(throwing: error)
        default:
            break
        }
    }

    private func answer(_ connection: NWConnection) {
        connection.start(queue: .main)
        connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self] data, _, _, _ in
            let line = data.flatMap { String(data: $0, encoding: .utf8) }?.trimmingCharacters(in: .whitespacesAndNewlines)
            MainActor.assumeIsolated {
                let reply = self?.reply(to: line ?? "") ?? ""
                connection.send(content: Data((reply + "\n").utf8), completion: .contentProcessed { _ in
                    connection.cancel()
                })
            }
        }
    }

    private func reply(to line: String) -> String {
        let parts = line.split(separator: "\t", maxSplits: 1).map(String.init)
        let argument = parts.count > 1 ? parts[1] : nil
        guard let head = parts.first, head.hasPrefix(token + " ") else { return "" }
        switch head.dropFirst(token.count + 1) {
        case "choose":
            return folders.choose(startingIn: argument) ?? ""
        case "login-item":
            return LoginItem.answer(turning: argument)
        default:
            return ""
        }
    }
}

/// Opening the app when someone logs in to the Mac, so the server is back after a restart. Off until the person turns
/// it on in the web app's settings: App Review doesn't allow an app to start itself at login unasked.
enum LoginItem {
    @MainActor
    static func answer(turning argument: String?) -> String {
        let service = SMAppService.mainApp
        do {
            switch argument {
            case "on" where service.status != .enabled: try service.register()
            case "off" where service.status != .notRegistered: try service.unregister()
            default: break
            }
        } catch {
            // Registering can fail while macOS waits for approval; the status below says so.
        }
        switch service.status {
        case .enabled: return "on"
        case .requiresApproval: return "approval"
        default: return "off"
        }
    }
}
