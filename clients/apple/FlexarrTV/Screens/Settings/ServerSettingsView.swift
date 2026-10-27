import FlexarrKit
import SwiftUI

/// The server this Apple TV uses: where it is, how it's reached now, and what it's running.
struct ServerSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var status: SystemStatus?
    @State private var error: String?
    @State private var checking = false

    var body: some View {
        SettingsPage(title: "Server", summary: summary) {
            SettingsGroupLabel("Connection")
            SettingsFact("Address", address(model.credentials?.address))
            if let remote = model.credentials?.remoteAddress {
                SettingsFact("Away from home", address(remote))
                SettingsFact("Connected", model.client?.addresses.isAway == true ? "Away, over Tailscale" : "At home")
            }

            SettingsGroupLabel("Server")
            if let status {
                SettingsFact("Version", ServerVersion.short(status.version))
                SettingsFact("Running on", status.osDescription)
                SettingsFact("Up since", status.startTime.formatted(date: .abbreviated, time: .shortened))
            } else {
                SettingsFact("Status", error ?? "Checking")
            }
            SettingsFact("Libraries", model.libraries.map(\.name).joined(separator: ", "))

            SettingsGroupLabel("Error reports")
            SettingsFact("Error reports", errorReportsOn ? "On" : "Off")
            Text(ErrorReports.privacyNote)
                .font(.system(size: 24))
                .foregroundStyle(Color.usher)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 32)

            // The only control here, so focus has somewhere to land; it also tells whether the server's still up.
            SettingsRow(title: checking ? "Checking…" : "Check again", symbol: "arrow.clockwise",
                        help: "Ask the server how it is now.") {
                Task { await load() }
            }
            .disabled(checking)
            .padding(.top, 30)

            SettingsLinkRow(title: "Send diagnostics", symbol: "stethoscope",
                            help: "Send this Apple TV's recent log to your server, to look into a problem.") {
                DiagnosticsSettingsView()
            }
        }
        .task { await load() }
    }

    /// As the server says now, or as this Apple TV last heard while it can't be asked.
    private var errorReportsOn: Bool {
        status.map { $0.errorReports?.enabled == true } ?? ErrorReports.isOn
    }

    private var summary: String {
        model.credentials?.remoteAddress == nil
            ? "To watch away from home, turn on remote access with Tailscale in Settings › Access on the web. This Apple TV picks the address up by itself."
            : "Away from home, this Apple TV reaches the server over Tailscale, which needs to be connected on it."
    }

    /// Without its scheme, which says nothing here.
    private func address(_ url: URL?) -> String {
        url.map { $0.absoluteString.replacingOccurrences(of: "^https?://", with: "", options: .regularExpression) } ?? ""
    }

    private func load() async {
        guard let client = model.client else { return }
        checking = true
        defer { checking = false }
        do {
            status = try await client.systemStatus()
            error = nil
        } catch {
            status = nil
            self.error = ConnectionError(error).message
        }
    }
}
