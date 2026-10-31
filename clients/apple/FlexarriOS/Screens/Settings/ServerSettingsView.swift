import FlexarrKit
import SwiftUI

struct ServerSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var status: SystemStatus?
    @State private var error: String?

    @State private var confirmLeave = false

    var body: some View {
        List {
            if let guest = model.credentials?.guest {
                Section {
                    row("Server", "\(guest.ownerName)’s Flexarr")
                    row("Connection", connection)
                    Button("Leave", role: .destructive) { confirmLeave = true }
                        .listRowBackground(Color.seat)
                } header: {
                    Text("Connected by invite")
                } footer: {
                    Text("Leaving signs this \(ThisDevice.name) out. Joining again takes a new invite.")
                }
                .confirmationDialog("Leave \(guest.ownerName)’s Flexarr?", isPresented: $confirmLeave, titleVisibility: .visible) {
                    Button("Leave", role: .destructive) { model.signOut() }
                }
            } else {
            Section {
                address("Address", model.credentials?.address)
                if let remote = model.credentials?.remoteAddress {
                    address("Away from home", remote)
                    row("Connected", model.client?.addresses.isAway == true ? "Away, over Tailscale" : "At home")
                }
            } footer: {
                Text(model.credentials?.remoteAddress == nil
                     ? "To use Flexarr away from home, turn on remote access with Tailscale in Settings › Access on the web; this \(ThisDevice.name) picks the address up by itself."
                     : "Away from home, this \(ThisDevice.name) reaches the server over Tailscale, which needs to be connected on it.")
            }
            }

            Section {
                if let status {
                    row("Version", ServerVersion.short(status.version))
                    row("Running on", status.osDescription)
                    row("Up since", status.startTime.formatted(date: .abbreviated, time: .shortened))
                } else if let error {
                    row("Status", error)
                } else {
                    row("Status", "Checking")
                }
                row("Libraries", model.libraries.map(\.name).joined(separator: ", "))
            }

            Section {
                row("Error reports", errorReportsOn ? "On" : "Off")
            } footer: {
                Text(ErrorReports.privacyNote)
            }

            Section {
                SettingsLink("Send diagnostics", icon: "stethoscope") { DiagnosticsSettingsView() }
            } footer: {
                Text("Send this \(ThisDevice.name)'s recent log to your server, to look into a problem.")
            }
        }
        .settingsPage("Server")
        .task { await load() }
        .refreshable { await load() }
    }

    /// A guest's tunnel right now: straight to the host's server, or through a relay.
    private var connection: String {
        switch GuestTunnel.shared.status()?.path {
        case "direct": "Direct"
        case "peer-relay": "Through the server’s relay"
        case "derp": "Through a relay (slower)"
        default: "Connecting"
        }
    }

    /// As the server says now, or as this device last heard while it can't be asked.
    private var errorReportsOn: Bool {
        status.map { $0.errorReports?.enabled == true } ?? ErrorReports.isOn
    }

    private func row(_ label: String, _ value: String) -> some View {
        LabeledContent(label) {
            Text(value)
                .foregroundStyle(Color.screen)
                .multilineTextAlignment(.trailing)
                .lineLimit(2)
        }
        .listRowBackground(Color.seat)
    }

    /// An address on one line, without its scheme: a long one loses its middle rather than wrapping
    /// under its label, and can be copied whole.
    private func address(_ label: String, _ url: URL?) -> some View {
        let text = url.map { $0.absoluteString.replacingOccurrences(of: "^https?://", with: "", options: .regularExpression) } ?? ""
        return LabeledContent(label) {
            Text(text)
                .foregroundStyle(Color.screen)
                .lineLimit(1)
                .truncationMode(.middle)
                .minimumScaleFactor(0.8)
                .textSelection(.enabled)
        }
        .listRowBackground(Color.seat)
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            status = try await client.systemStatus()
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}
