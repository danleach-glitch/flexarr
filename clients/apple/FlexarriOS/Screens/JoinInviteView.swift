import FlexarrKit
import SwiftUI

extension GuestInvite: @retroactive Identifiable {
    public var id: String { token }
}

/// "Join Dan's Flexarr?": asked before an invite link (or a pasted invite) does anything, since a link can come from
/// any app.
struct JoinInviteView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let invite: GuestInvite
    @State private var joining = false
    @State private var problem: String?

    var body: some View {
        VStack(spacing: 22) {
            BrandMark(size: 56)
                .padding(.top, 8)
            VStack(spacing: 8) {
                Text("Join \(invite.ownerName)’s Flexarr?")
                    .font(.title2.weight(.bold))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                Text("You’ll watch as \(invite.profileName).")
                    .font(.body)
                    .foregroundStyle(Color.usher)
                    .multilineTextAlignment(.center)
                if model.credentials != nil {
                    Text("This \(ThisDevice.name) leaves the server it’s connected to now.")
                        .font(.footnote)
                        .foregroundStyle(Color.usher)
                        .multilineTextAlignment(.center)
                }
            }
            if let problem {
                Text(problem)
                    .font(.callout.weight(.medium))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
            }
            VStack(spacing: 10) {
                Button {
                    Task { await join() }
                } label: {
                    Group {
                        if joining { ProgressView() } else { Text("Join") }
                    }
                    .frame(maxWidth: .infinity)
                }
                .buttonStyle(ActionButtonStyle(prominent: true))
                .disabled(joining)
                Button { dismiss() } label: { Text("Not Now").frame(maxWidth: .infinity) }
                    .buttonStyle(ActionButtonStyle(prominent: false))
                    .disabled(joining)
            }
        }
        .padding(28)
        .presentationDetents([.medium])
        .presentationBackground(Color.room)
        .interactiveDismissDisabled(joining)
    }

    private func join() async {
        joining = true
        problem = nil
        defer { joining = false }
        do {
            try await model.join(invite)
            dismiss()
        } catch {
            problem = error.localizedDescription
        }
    }
}

/// On the connect screen: an invite copied from flexarr.io/join (the app wasn't installed when its link was opened).
struct PasteInvite: View {
    @Environment(AppModel.self) private var model
    @State private var notAnInvite = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Invited by someone?")
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(Color.usher)
            PasteButton(payloadType: String.self) { texts in
                if let invite = texts.lazy.compactMap(GuestInvite.parse).first {
                    notAnInvite = false
                    model.pendingInvite = invite
                } else {
                    notAnInvite = true
                }
            }
            .labelStyle(.titleAndIcon)
            .tint(.silver)
            Text(notAnInvite ? "That isn’t an invite. Copy it again from the message." : "Or open the invite’s link, or scan its code with the Camera.")
                .font(.footnote)
                .foregroundStyle(notAnInvite ? Color.screen : Color.usher)
        }
    }
}
