import FlexarrKit
import SwiftUI

/// The server this Apple TV uses: where it is, how it's reached now, and what it's running.
struct ServerSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var status: SystemStatus?
    @State private var error: String?
    @State private var checking = false
    @State private var confirmLeave = false

    var body: some View {
        SettingsPage(title: t("Server"), summary: summary) {
            SettingsGroupLabel(t("Connection"))
            if let guest = model.credentials?.guest {
                // A family guest: no address to show, just whose server, and a way to leave it.
                SettingsFact(t("Connected by invite"), model.guestServerTitle(guest))
                SettingsRow(title: t("Leave"), symbol: "rectangle.portrait.and.arrow.right",
                            help: t("Stop watching {server} here. Joining again takes a new code.", ["server": model.guestServerTitle(guest)])) {
                    confirmLeave = true
                }
                .leaveGuestConfirmation(isPresented: $confirmLeave, server: model.guestServerTitle(guest)) { model.signOut() }
            } else {
            if let name = model.serverName { SettingsFact(t("Name"), name) }
            SettingsFact(t("Address"), address(model.credentials?.address))
            }
            if let remote = model.credentials?.remoteAddress {
                SettingsFact(t("Away from home"), address(remote))
                SettingsFact(t("Connected"), model.client?.addresses.isAway == true ? t("Away, over Tailscale") : t("At home"))
            }

            SettingsGroupLabel(t("Server"))
            if let status {
                SettingsFact(t("Version"), ServerVersion.short(status.version))
                SettingsFact(t("Running on"), status.osDescription)
                SettingsFact(t("Up since"), status.startTime.formatted(Date.FormatStyle(date: .abbreviated, time: .shortened, locale: Localization.locale)))
            } else {
                SettingsFact(t("Status"), error ?? t("Checking"))
            }
            SettingsFact(t(Words.libraries), model.libraries.map(\.name).joined(separator: ", "))

            SettingsGroupLabel(t("Error reports"))
            SettingsFact(t("Error reports"), errorReportsOn ? t("On") : t("Off"))
            Text(ErrorReports.privacyNote)
                .font(.system(size: 24))
                .foregroundStyle(Color.usher)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 32)

            // The only control here, so focus has somewhere to land; it also tells whether the server's still up.
            SettingsRow(title: checking ? t("Checking…") : t("Check again"), symbol: "arrow.clockwise",
                        help: t("Ask the server how it is now.")) {
                Task { await load() }
            }
            .disabled(checking)
            .padding(.top, 30)

            SettingsLinkRow(title: t("Send diagnostics"), symbol: "stethoscope",
                            help: t("Send this Apple TV’s recent log to your server, to look into a problem.")) {
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
            ? t("To watch away from home, turn on remote access with Tailscale in Settings › Remote access on the web. This Apple TV picks the address up by itself.")
            : t("Away from home, this Apple TV reaches the server over Tailscale, which needs to be connected on it.")
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
