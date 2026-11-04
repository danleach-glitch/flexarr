import FlexarrKit
import SwiftUI

struct ServerSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var status: SystemStatus?
    @State private var error: String?

    @State private var confirmLeave = false
    /// A guest's tunnel right now (see `connection`), asked off the main thread.
    @State private var path: String?

    var body: some View {
        List {
            if let guest = model.credentials?.guest {
                Section {
                    row(t("Server"), model.guestServerTitle(guest))
                    row(t("Connection"), connection)
                    Button(t("Leave"), role: .destructive) { confirmLeave = true }
                        .listRowBackground(Color.seat)
                } header: {
                    Text(t("Connected by invite"))
                }
                .leaveGuestConfirmation(isPresented: $confirmLeave, server: model.guestServerNaming(guest)) { model.signOut() }
            } else {
            Section {
                if let name = model.serverName { row(t("Name"), name) }
                address(t("Address"), model.credentials?.address)
                if let remote = model.credentials?.remoteAddress {
                    address(t("Away from home"), remote)
                    row(t("Connected"), model.client?.addresses.isAway == true ? t("Away, over Tailscale") : t("At home"))
                }
            } footer: {
                Text(model.credentials?.remoteAddress == nil
                     ? t("To use Flexarr away from home, turn on remote access with Tailscale in Settings › Remote access on the web; this {device} picks the address up by itself.", ["device": ThisDevice.name])
                     : t("Away from home, this {device} reaches the server over Tailscale, which needs to be connected on it.", ["device": ThisDevice.name]))
            }
            }

            Section {
                if let status {
                    row(t("Version"), ServerVersion.short(status.version))
                    row(t("Running on"), status.osDescription)
                    row(t("Up since"), status.startTime.formatted(Date.FormatStyle(date: .abbreviated, time: .shortened, locale: Localization.locale)))
                } else if let error {
                    row(t("Status"), error)
                } else {
                    row(t("Status"), t("Checking"))
                }
                row(t(Words.libraries), model.libraries.map(\.name).joined(separator: ", "))
            }

            Section {
                row(t("Error reports"), errorReportsOn ? t("On") : t("Off"))
            } footer: {
                Text(ErrorReports.privacyNote)
            }

            Section {
                SettingsLink(t("Send diagnostics"), icon: "stethoscope") { DiagnosticsSettingsView() }
            } footer: {
                Text(t("Send this {device}’s recent log to your server, to look into a problem.", ["device": ThisDevice.name]))
            }
        }
        .settingsPage(t("Server"))
        .task { await load() }
        .refreshable { await load() }
    }

    /// A guest's tunnel right now: straight to the host's server, or through a relay.
    private var connection: String {
        switch path {
        case "direct": t("Direct")
        case "peer-relay": t("Through the server’s relay")
        case "derp": t("Through a relay (slower)")
        default: t("Connecting")
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
        if model.credentials?.isGuest == true { path = await GuestTunnel.shared.connectionPath() }
        guard let client = model.client else { return }
        do {
            status = try await client.systemStatus()
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}
