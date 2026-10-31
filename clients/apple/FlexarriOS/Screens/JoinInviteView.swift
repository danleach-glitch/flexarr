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

/// On the connect screen, for a family guest: a code to give whoever invited them.
struct InvitedBySomeone: View {
    let onCode: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Invited by someone?")
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(Color.usher)
            ChoiceRow(systemImage: "key", title: "Get a code", action: onCode)
        }
    }
}

/// "Invited by someone?": the code to read or text to whoever invited this iPhone or iPad (see `GuestCodeWaiter`).
struct GuestCodeView: View {
    @Environment(AppModel.self) private var model
    let onBack: () -> Void
    @State private var waiter = GuestCodeWaiter()

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            if let joining = waiter.joining {
                HStack(spacing: 12) {
                    ProgressView()
                    Text("Joining \(joining.ownerName)’s Flexarr…")
                        .font(.headline)
                        .foregroundStyle(Color.screen)
                }
            } else {
                Text("Give this code to the person who invited you")
                    .font(.headline)
                    .foregroundStyle(Color.usher)
                Text(waiter.code?.display ?? "····-····")
                    .font(.system(size: 46, weight: .bold, design: .monospaced))
                    .foregroundStyle(Color.screen)
                    .textSelection(.enabled)
                    .accessibilityLabel(waiter.code.map { $0.code.map(String.init).joined(separator: " ") } ?? "Getting a code")
                if let code = waiter.code {
                    ShareLink(item: code.display, message: Text("My Flexarr code")) {
                        Label("Send it", systemImage: "square.and.arrow.up")
                    }
                    .font(.body.weight(.semibold))
                }
                Text("They type it into Settings › Access in Flexarr. Keep this screen open.")
                    .font(.footnote)
                    .foregroundStyle(Color.usher)
            }
            if let problem = waiter.problem {
                Text(problem)
                    .font(.callout.weight(.medium))
                    .foregroundStyle(Color.screen)
            }
            Button("Back", action: onBack)
                .font(.body.weight(.semibold))
                .disabled(waiter.joining != nil)
        }
        .task { await waiter.run(model: model) }
    }
}
