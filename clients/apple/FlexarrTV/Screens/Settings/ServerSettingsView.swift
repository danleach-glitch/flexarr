import FlexarrKit
import SwiftUI

/// The server this Apple TV uses: where it is, how it's reached now, and what it's running.
struct ServerSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var status: SystemStatus?
    @State private var error: String?
    @State private var checking = false
    @State private var confirmLeave = false
    @FocusState private var focusesCheck: Bool
    @State private var openedAt = Date.now
    /// Use away from home's code is showing.
    @State private var usingAway = false

    var body: some View {
        SettingsPage(title: t("Server"), summary: summary) {
            SettingsGroupLabel(t("Connection"))
            if let guest = model.credentials?.guest, model.credentials?.isGuest != true {
                // A household device away from home: no address to show, just whose server. Sign out is on Settings.
                SettingsFact(t("Away from home"), model.guestServerTitle(guest))
            } else if let guest = model.credentials?.guest {
                // A family guest: no address to show, just whose server, and a way to leave it.
                SettingsFact(t("Connected by invite"), model.guestServerTitle(guest))
                SettingsRow(title: t("Leave"), symbol: "rectangle.portrait.and.arrow.right",
                            help: t("{kind, select, named {Stop watching {server} here. Joining again takes a new code.} owner {Stop watching {owner}’s Flexarr here. Joining again takes a new code.} other {Stop watching this Flexarr here. Joining again takes a new code.}}", model.guestServerNaming(guest).values)) {
                    confirmLeave = true
                }
                .leaveGuestConfirmation(isPresented: $confirmLeave, server: model.guestServerNaming(guest)) { model.signOut() }
            } else {
            if let name = model.serverName { SettingsFact(t("Name"), name) }
            SettingsFact(t("Address"), address(model.credentials?.address))
            if model.offersUseAwayFromHome(serverVersion: status?.version) {
                SettingsRow(title: t("Use away from home"), symbol: "key",
                            help: t("Watch here away from home without Tailscale. Whoever runs Flexarr types a code.")) {
                    usingAway = true
                }
            }
            }
            if let remote = model.credentials?.remoteAddress {
                SettingsFact(t("Away from home"), address(remote))
                SettingsFact(t("Connected"), model.client?.addresses.isAway == true ? t("Away, over Tailscale") : t("At home"))
            }

            SettingsGroupLabel(t("Server"))
            if let status {
                SettingsFact(t(Words.version), ServerVersion.short(status.version))
                SettingsFact(t("Running on"), status.osDescription)
                SettingsFact(t("Up since"), status.startTime.formatted(Date.FormatStyle(date: .abbreviated, time: .shortened, locale: Localization.locale)))
            } else if let error {
                SettingsFact(t("Status"), error)
            } else {
                // The three facts' room while they're asked for, so the rows below (and focus) don't jump down when
                // they come.
                SettingsFact(t("Status"), t("Checking"))
                Color.clear.frame(height: 74 * 2 + 10)
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
            // Not disabled while it checks: focus would skip it for the row below as the page opens.
            SettingsRow(title: checking ? t("Checking…") : t("Check again"), symbol: "arrow.clockwise",
                        help: t("Ask the server how it is now.")) {
                guard !checking else { return }
                Task { await load() }
            }
            .focused($focusesCheck)
            .padding(.top, 30)

            SettingsLinkRow(title: t("Send diagnostics"), symbol: "stethoscope",
                            help: t("Send this Apple TV’s recent log to your server, to look into a problem.")) {
                DiagnosticsSettingsView()
            }
        }
        .onAppear { openedAt = .now }
        // The same code as Join someone’s Flexarr. Joined, the tunnel's sign-in takes the place of the one at home;
        // Back leaves that as it was.
        .fullScreenCover(isPresented: $usingAway) {
            GuestCode(onBack: { usingAway = false })
                .frame(width: 820)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(RoomBackground(ambient: .silver, intensity: 0.22))
        }
        .onChange(of: model.credentials?.usesTunnel) { _, tunnel in
            if tunnel == true { usingAway = false }
        }
        .task {
            await load()
            // Wherever focus landed as the page opened, it's on "Check again" now, in view, unless it's been moved.
            if model.credentials?.isGuest != true, FocusMove.untouched(since: openedAt) { focusesCheck = true }
        }
    }

    /// As the server says now, or as this Apple TV last heard while it can't be asked.
    private var errorReportsOn: Bool {
        status.map { $0.errorReports?.enabled == true } ?? ErrorReports.isOn
    }

    private var summary: String {
        model.credentials?.isHouseholdAway == true
            ? t("Away from home, Flexarr uses its own connection. AirPlay is off on it.")
            : model.credentials?.usesTunnel == true
            ? t("Connected over the internet. AirPlay is off on this connection.")
            : model.credentials?.remoteAddress == nil
            ? t("To watch away from home, turn on remote access with Tailscale in Settings › Remote access on the web. This Apple TV picks the address up by itself.")
            : model.credentials?.remoteConfirmed == false
            ? t("This Apple TV hasn’t reached the server over Tailscale yet.")
            : t("Away from home, this Apple TV reaches the server over Tailscale, which needs to be connected on it.")
    }

    /// Without its scheme, which says nothing here.
    private func address(_ url: URL?) -> String {
        url.map { $0.absoluteString.replacingOccurrences(of: "^https?://", with: "", options: .regularExpression) } ?? ""
    }

    private func load() async {
        // Says here whether the away address has been reached, so it's asked again if it hasn't (beside the status,
        // which needn't wait for it).
        Task { await model.checkAwayAddress() }
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
