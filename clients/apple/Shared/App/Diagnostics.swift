import FlexarrKit
import Foundation
import Observation
#if canImport(UIKit)
import UIKit
#else
import AppKit
#endif

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
        case .sent(let id): t("Sent to your server ({id}). Mention that if you ask for help.", ["id": id])
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
            state = .failed(t("This server doesn’t take diagnostics yet. Update Flexarr on it, then try again."))
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
        #if os(macOS)
        let system = "macOS \(ProcessInfo.processInfo.operatingSystemVersionString)"
        #else
        let system = "\(UIDevice.current.systemName) \(UIDevice.current.systemVersion)"
        #endif
        var lines = [
            "Flexarr for \(ThisDevice.name) \(Bundle.main.shortVersion) (\(build))",
            "\(system) on \(hardwareModel)",
            "Sent \(Date().formatted(.iso8601))",
        ]
        if let host = model.credentials?.address.host() {
            lines.append(model.serverName.map { "Server: \($0) (\(host))" } ?? "Server: \(host)")
        }
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
        #if os(macOS)
        // "Mac15,12": uname only says "arm64" on a Mac.
        var size = 0
        sysctlbyname("hw.model", nil, &size, nil, 0)
        var model = [CChar](repeating: 0, count: max(size, 1))
        if size > 0, sysctlbyname("hw.model", &model, &size, nil, 0) == 0 {
            return String(decoding: model.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self)
        }
        #endif
        var system = utsname()
        uname(&system)
        return withUnsafeBytes(of: &system.machine) { String(decoding: $0.prefix { $0 != 0 }, as: UTF8.self) }
    }
}
