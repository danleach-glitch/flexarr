import FlexarrKit
import SwiftUI

/// The server can't be reached (no network, or no answer within a few seconds). There's nothing to play without it on
/// Apple TV, so it says so straight away, tries again by itself, and carries on once the server answers. Sign out (Leave,
/// for a family guest) is the way out when the server has gone for good, or moved.
struct OfflineView: View {
    @Environment(AppModel.self) private var model
    @State private var checking = false
    @State private var confirmSignOut = false
    @State private var confirmLeave = false

    private var isGuest: Bool { model.credentials?.isGuest == true }

    /// The server's name (a guest's host: "Dan’s Flexarr" when it has none), or nil for "this Flexarr".
    private var server: String? {
        isGuest ? model.guestServerTitle(model.credentials?.guest) : model.serverName
    }

    var body: some View {
        VStack(spacing: 30) {
            Image(systemName: "wifi.slash")
                .font(.system(size: 80))
                .foregroundStyle(Color.silver)
                .accessibilityHidden(true)
            // As the tunnel's problem screen words it.
            Text(server.map { t("Can’t reach {server}", ["server": $0]) } ?? t("Can’t reach this Flexarr"))
                .font(.system(size: 52, weight: .bold))
                .foregroundStyle(Color.screen)
                .accessibilityAddTraits(.isHeader)
            Text(server.map { t("Check this device’s internet, or that {server} is on.", ["server": $0]) }
                 ?? t("Check this device’s internet, or that its server is on."))
                .font(.system(size: 30))
                .foregroundStyle(Color.usher)
            HStack(spacing: 30) {
                // Stays focusable while it tries: a press meanwhile simply waits for the same check.
                Button(t("Try again")) {
                    guard !checking else { return }
                    Task {
                        checking = true
                        await model.checkConnection(ignoringNetwork: true)
                        checking = false
                    }
                }
                .buttonStyle(ActionButtonStyle(prominent: true))
                Button(isGuest ? t("Leave") : t("Sign out")) {
                    if isGuest { confirmLeave = true } else { confirmSignOut = true }
                }
                .buttonStyle(ActionButtonStyle())
            }
            .padding(.top, 10)
        }
        .multilineTextAlignment(.center)
        .padding(80)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityIdentifier("offline")
        .confirmationDialog(t("Sign out of this server?"), isPresented: $confirmSignOut, titleVisibility: .visible) {
            Button(t("Sign out"), role: .destructive) { model.signOut() }
        }
        .leaveGuestConfirmation(isPresented: $confirmLeave, server: model.guestServerTitle(model.credentials?.guest)) { model.signOut() }
    }
}

private extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}
