import SwiftUI

/// A family guest's app couldn't reach its host's server through the tunnel: Try again, Leave (which forgets the
/// invite, so a new one can be used), and on iPhone the downloads, which play without the server.
struct TunnelProblemView: View {
    @Environment(AppModel.self) private var model
    /// Opens this device's downloads (iPhone, when there are some).
    var onDownloads: (() -> Void)?
    @State private var confirmLeave = false

    private var owner: String { model.credentials?.guest?.ownerName ?? "the host" }

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "wifi.exclamationmark")
                .font(.system(size: 44))
                .foregroundStyle(Color.silver)
            Text("Can’t reach \(owner)’s Flexarr")
                .font(.title2.weight(.semibold))
                .multilineTextAlignment(.center)
            Text("Check this device’s internet, or that \(owner)’s server is on.")
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
        .leaveGuestConfirmation(isPresented: $confirmLeave, owner: owner) { model.signOut() }
    }

    @ViewBuilder private var buttons: some View {
        Button("Try again") { Task { await model.retryGuestTunnel() } }
            .buttonStyle(ActionButtonStyle(prominent: true))
        if let onDownloads {
            Button("Downloads", action: onDownloads)
                .buttonStyle(ActionButtonStyle())
        }
        Button("Leave", role: .destructive) { confirmLeave = true }
            .buttonStyle(ActionButtonStyle())
    }
}

extension View {
    /// "Leave Dan’s Flexarr?", asked before a family guest leaves its host (Settings › Server, or the problem screen).
    func leaveGuestConfirmation(isPresented: Binding<Bool>, owner: String, onLeave: @escaping () -> Void) -> some View {
        modifier(LeaveGuestConfirmation(isPresented: isPresented, owner: owner, onLeave: onLeave))
    }
}

private struct LeaveGuestConfirmation: ViewModifier {
    @Binding var isPresented: Bool
    let owner: String
    let onLeave: () -> Void

    func body(content: Content) -> some View {
        #if os(tvOS)
        // Its own screen rather than the system's dialog, so the focus can start on Cancel.
        content.fullScreenCover(isPresented: $isPresented) {
            LeaveGuestPrompt(owner: owner, isPresented: $isPresented, onLeave: onLeave)
        }
        #else
        content.confirmationDialog("Leave \(owner)’s Flexarr?", isPresented: $isPresented, titleVisibility: .visible) {
            Button("Leave", role: .destructive, action: onLeave)
        } message: {
            // Signing out deletes the iPhone's downloads.
            Text("Joining again takes a new code. Downloads are deleted.")
        }
        #endif
    }
}

#if os(tvOS)
private struct LeaveGuestPrompt: View {
    let owner: String
    @Binding var isPresented: Bool
    let onLeave: () -> Void
    @FocusState private var focus: Choice?

    private enum Choice { case cancel, leave }

    var body: some View {
        VStack(spacing: 36) {
            Text("Leave \(owner)’s Flexarr?")
                .font(.system(size: 52, weight: .bold))
                .foregroundStyle(Color.screen)
                .multilineTextAlignment(.center)
            Text("Joining again takes a new code.")
                .font(.system(size: 30))
                .foregroundStyle(Color.usher)
            HStack(spacing: 30) {
                Button("Cancel") { isPresented = false }
                    .buttonStyle(ActionButtonStyle(prominent: true))
                    .focused($focus, equals: .cancel)
                Button("Leave") {
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
