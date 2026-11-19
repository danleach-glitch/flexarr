import FlexarrKit
import SwiftUI

/// The app couldn't reach its host's server through the tunnel: Try again, Leave (Sign out, for a household device away
/// from home; either forgets the invite, so a new one can be used), and on iPhone the downloads, which play without the
/// server.
struct TunnelProblemView: View {
    @Environment(AppModel.self) private var model
    /// Opens this device's downloads (iPhone, when there are some).
    var onDownloads: (() -> Void)?
    @State private var confirmLeave = false
    @State private var confirmSignOut = false

    /// A family guest leaves; a household device away from home signs out, as it would at home.
    private var isGuest: Bool { model.credentials?.isGuest == true }

    /// The host's server: its name, or "Dan’s Flexarr".
    private var server: ServerNaming { model.guestServerNaming(model.credentials?.guest) }

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "wifi.exclamationmark")
                .font(.system(size: 44))
                .foregroundStyle(Color.silver)
            Text(t("{kind, select, named {Can’t reach {server}} owner {Can’t reach {owner}’s Flexarr} other {Can’t reach this Flexarr}}", server.values))
                .font(.title2.weight(.semibold))
                .multilineTextAlignment(.center)
            Text(t("{kind, select, named {Check this device’s internet, or that {server} is on.} owner {Check this device’s internet, or that {owner}’s Flexarr is on.} other {Check this device’s internet, or that its server is on.}}", server.values))
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
        .confirmationDialog(t("Sign out of this server?"), isPresented: $confirmSignOut, titleVisibility: .visible) {
            Button(t("Sign out"), role: .destructive) { model.signOut() }
        } message: {
            #if os(tvOS)
            Text(t("Joining again takes a new code."))
            #else
            Text(t("Joining again takes a new code. Downloads are deleted."))
            #endif
        }
    }

    @ViewBuilder private var buttons: some View {
        Button(t("Try again")) { Task { await model.retryGuestTunnel() } }
            .buttonStyle(ActionButtonStyle(prominent: true))
        if let onDownloads {
            Button(t(Words.downloads), action: onDownloads)
                .buttonStyle(ActionButtonStyle())
        }
        Button(isGuest ? t("Leave") : t("Sign out"), role: .destructive) {
            if isGuest { confirmLeave = true } else { confirmSignOut = true }
        }
        .buttonStyle(ActionButtonStyle())
    }
}

extension View {
    /// "Signed in as Claire": said once, after joining as a household device away from home, so it's plain this is
    /// their own Flexarr and not a guest's visit.
    func awayWelcome(_ model: AppModel) -> some View {
        modifier(AwayWelcomeAlert(model: model))
    }

    /// "Leave Smith House?" (or "Leave Dan’s Flexarr?"), asked before a family guest leaves its host (Settings ›
    /// Server, or the problem screen).
    func leaveGuestConfirmation(isPresented: Binding<Bool>, server: ServerNaming, onLeave: @escaping () -> Void) -> some View {
        modifier(LeaveGuestConfirmation(isPresented: isPresented, server: server, onLeave: onLeave))
    }
}

private struct AwayWelcomeAlert: ViewModifier {
    let model: AppModel

    func body(content: Content) -> some View {
        content.alert(model.awayWelcome.map { t("Signed in as {profile}", ["profile": $0.profile]) } ?? "", isPresented: Binding(
            get: { model.awayWelcome != nil },
            set: { if !$0 { model.awayWelcome = nil } }
        )) {
            Button(t("OK"), role: .cancel) {}
        } message: {
            if let welcome = model.awayWelcome {
                Text(t("{kind, select, named {{server} works here as it does at home.} owner {{owner}’s Flexarr works here as it does at home.} other {Flexarr works here as it does at home.}}", welcome.server.values))
            }
        }
    }
}

private struct LeaveGuestConfirmation: ViewModifier {
    @Binding var isPresented: Bool
    let server: ServerNaming
    let onLeave: () -> Void

    func body(content: Content) -> some View {
        #if os(tvOS)
        // Its own screen rather than the system's dialog, so the focus can start on Cancel.
        content.fullScreenCover(isPresented: $isPresented) {
            LeaveGuestPrompt(server: server, isPresented: $isPresented, onLeave: onLeave)
        }
        #else
        content.confirmationDialog(t("{kind, select, named {Leave {server}?} owner {Leave {owner}’s Flexarr?} other {Leave this Flexarr?}}", server.values), isPresented: $isPresented, titleVisibility: .visible) {
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
    let server: ServerNaming
    @Binding var isPresented: Bool
    let onLeave: () -> Void
    @FocusState private var focus: Choice?

    private enum Choice { case cancel, leave }

    var body: some View {
        VStack(spacing: 36) {
            Text(t("{kind, select, named {Leave {server}?} owner {Leave {owner}’s Flexarr?} other {Leave this Flexarr?}}", server.values))
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
