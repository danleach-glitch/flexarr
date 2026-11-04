import FlexarrKit
import SwiftUI

/// A family guest's app couldn't reach its host's server through the tunnel: Try again, Leave (which forgets the
/// invite, so a new one can be used), and on iPhone the downloads, which play without the server.
struct TunnelProblemView: View {
    @Environment(AppModel.self) private var model
    /// Opens this device's downloads (iPhone, when there are some).
    var onDownloads: (() -> Void)?
    @State private var confirmLeave = false

    /// The host's server: its name, or "Dan’s Flexarr".
    private var server: String { model.guestServerTitle(model.credentials?.guest) }

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "wifi.exclamationmark")
                .font(.system(size: 44))
                .foregroundStyle(Color.silver)
            Text(t("Can’t reach {server}", ["server": server]))
                .font(.title2.weight(.semibold))
                .multilineTextAlignment(.center)
            Text(t("Check this device’s internet, or that {server} is on.", ["server": server]))
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            // Side by side while they fit, one above another at larger text sizes.
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 16) { buttons }
                VStack(spacing: 12) { buttons }
            }
        }
        .padding(40)
        .frame(maxWidth: 560)
        .leaveGuestConfirmation(isPresented: $confirmLeave, server: server) { model.signOut() }
    }

    @ViewBuilder private var buttons: some View {
        Button(t("Try again")) { Task { await model.retryGuestTunnel() } }
            .buttonStyle(ActionButtonStyle(prominent: true))
        if let onDownloads {
            Button(t(Words.downloads), action: onDownloads)
                .buttonStyle(ActionButtonStyle())
        }
        Button(t("Leave"), role: .destructive) { confirmLeave = true }
            .buttonStyle(ActionButtonStyle())
    }
}

extension View {
    /// "Leave Smith House?" (or "Leave Dan’s Flexarr?"), asked before a family guest leaves its host (Settings ›
    /// Server, or the problem screen).
    func leaveGuestConfirmation(isPresented: Binding<Bool>, server: String, onLeave: @escaping () -> Void) -> some View {
        modifier(LeaveGuestConfirmation(isPresented: isPresented, server: server, onLeave: onLeave))
    }
}

private struct LeaveGuestConfirmation: ViewModifier {
    @Binding var isPresented: Bool
    let server: String
    let onLeave: () -> Void

    func body(content: Content) -> some View {
        #if os(tvOS)
        // Its own screen rather than the system's dialog, so the focus can start on Cancel.
        content.fullScreenCover(isPresented: $isPresented) {
            LeaveGuestPrompt(server: server, isPresented: $isPresented, onLeave: onLeave)
        }
        #else
        content.confirmationDialog(t("Leave {server}?", ["server": server]), isPresented: $isPresented, titleVisibility: .visible) {
            Button(t("Leave"), role: .destructive, action: onLeave)
        } message: {
            // Signing out deletes the iPhone's downloads.
            Text(t("Joining again takes a new code. Downloads are deleted."))
        }
        #endif
    }
}

#if os(tvOS)
private struct LeaveGuestPrompt: View {
    let server: String
    @Binding var isPresented: Bool
    let onLeave: () -> Void
    @FocusState private var focus: Choice?

    private enum Choice { case cancel, leave }

    var body: some View {
        VStack(spacing: 36) {
            Text(t("Leave {server}?", ["server": server]))
                .font(.system(size: 52, weight: .bold))
                .foregroundStyle(Color.screen)
                .multilineTextAlignment(.center)
            Text(t("Joining again takes a new code."))
                .font(.system(size: 30))
                .foregroundStyle(Color.usher)
            HStack(spacing: 30) {
                Button(t(Words.cancel)) { isPresented = false }
                    .buttonStyle(ActionButtonStyle(prominent: true))
                    .focused($focus, equals: .cancel)
                Button(t("Leave")) {
                    isPresented = false
                    onLeave()
                }
                .buttonStyle(ActionButtonStyle(prominent: false))
                .focused($focus, equals: .leave)
            }
            .padding(.top, 10)
        }
        .padding(80)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.room.ignoresSafeArea())
        .defaultFocus($focus, .cancel, priority: .userInitiated)
        .onAppear { focus = .cancel }
    }
}
#endif
