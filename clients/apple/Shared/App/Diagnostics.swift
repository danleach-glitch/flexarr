import FlexarrKit
import Foundation
import Observation
import UIKit

/// Settings › Send diagnostics: sends this device's log, with a note if there is one, to the server, and says how it
/// went. The log only ever goes to the signed-in server.
@MainActor
@Observable
final class DiagnosticsUpload {
    enum State: Equatable {
        case idle
        case sending
        /// The server's id for what was sent.
        case sent(String)
        case failed(String)
    }

    var note = ""
    private(set) var state = State.idle

    var isSending: Bool { state == .sending }

    /// The line under the button: how the last send went.
    var outcome: String? {
        switch state {
        case .idle, .sending: nil
        case .sent(let id): "Sent to your server (\(id)). Mention that if you ask for help."
        case .failed(let message): message
        }
    }

    func send(model: AppModel) async {
        guard !isSending, let client = model.client else { return }
        state = .sending
        let header = DiagnosticsHeader.make(model: model)
        do {
            let id = try await LogHub.shared.sendDiagnostics(client: client, header: header, note: note)
            state = .sent(id)
            note = ""
        } catch FlexarrClientError.unexpectedResponse, FlexarrClientError.http(status: 404, _), FlexarrClientError.http(status: 405, _) {
            state = .failed("This server doesn't take diagnostics yet. Update Flexarr on it, then try again.")
        } catch {
            state = .failed(ConnectionError(error).message)
        }
    }
}

/// The lines on top of a diagnostics upload: which app, which device, which server (its host only), who's watching.
enum DiagnosticsHeader {
    @MainActor
    static func make(model: AppModel) -> String {
        let info = Bundle.main.infoDictionary
        let build = info?["CFBundleVersion"] as? String ?? ""
        let device = UIDevice.current
        var lines = [
            "Flexarr for \(ThisDevice.name) \(Bundle.main.shortVersion) (\(build))",
            "\(device.systemName) \(device.systemVersion) on \(hardwareModel)",
            "Sent \(Date().formatted(.iso8601))",
        ]
        if let host = model.credentials?.address.host() { lines.append("Server: \(host)") }
        if let away = model.client?.addresses.isAway, model.credentials?.remoteAddress != nil {
            lines.append("Connected: \(away ? "away, over Tailscale" : "at home")")
        }
        if let profile = model.currentProfile { lines.append("Profile: \(profile.name)") }
        return lines.joined(separator: "\n")
    }

    /// "AppleTV14,1", "iPhone17,1": the exact model, which `UIDevice.model` ("iPhone") doesn't say.
    private static var hardwareModel: String {
        #if targetEnvironment(simulator)
        if let simulated = ProcessInfo.processInfo.environment["SIMULATOR_MODEL_IDENTIFIER"] { return "\(simulated) (simulator)" }
        #endif
        var system = utsname()
        uname(&system)
        return withUnsafeBytes(of: &system.machine) { String(decoding: $0.prefix { $0 != 0 }, as: UTF8.self) }
    }
}
