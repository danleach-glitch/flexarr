import SwiftUI

/// The server can't be reached (no network, or no answer within a few seconds). There's nothing to play without it on
/// Apple TV, so it says so straight away, tries again by itself, and carries on once the server answers. Sign out (Leave,
/// for a family guest) is the way out when the server has gone for good, or moved.
struct OfflineView: View {
    @Environment(AppModel.self) private var model
    @State private var checking = false
    @State private var confirmSignOut = false
    @State private var confirmLeave = false

    private var owner: String? {
        model.credentials?.guest?.ownerName.trimmingCharacters(in: .whitespaces).nilIfEmpty
    }

    var body: some View {
        VStack(spacing: 30) {
            Image(systemName: "wifi.slash")
                .font(.system(size: 80))
                .foregroundStyle(Color.silver)
                .accessibilityHidden(true)
            // As the tunnel's problem screen words it.
            Text(owner.map { "Can’t reach \($0)’s Flexarr" } ?? "Can’t reach this Flexarr")
                .font(.system(size: 52, weight: .bold))
                .foregroundStyle(Color.screen)
                .accessibilityAddTraits(.isHeader)
            Text(owner.map { "Check this device’s internet, or that \($0)’s server is on." } ?? "Check this device’s internet, or that its server is on.")
                .font(.system(size: 30))
                .foregroundStyle(Color.usher)
            HStack(spacing: 30) {
                // Stays focusable while it tries: a press meanwhile simply waits for the same check.
                Button("Try again") {
                    guard !checking else { return }
                    Task {
                        checking = true
                        await model.checkConnection(ignoringNetwork: true)
                        checking = false
                    }
                }
                .buttonStyle(ActionButtonStyle(prominent: true))
                Button(owner == nil ? "Sign out" : "Leave") {
                    if owner == nil { confirmSignOut = true } else { confirmLeave = true }
                }
                .buttonStyle(ActionButtonStyle())
            }
            .padding(.top, 10)
        }
        .multilineTextAlignment(.center)
        .padding(80)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityIdentifier("offline")
        .confirmationDialog("Sign out of this server?", isPresented: $confirmSignOut, titleVisibility: .visible) {
            Button("Sign out", role: .destructive) { model.signOut() }
        }
        .leaveGuestConfirmation(isPresented: $confirmLeave, owner: owner ?? "this") { model.signOut() }
    }
}

private extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}
