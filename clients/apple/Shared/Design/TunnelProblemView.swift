import SwiftUI

/// A family guest's app couldn't reach its host's server through the tunnel: why, with Try again and Leave (which
/// forgets the invite, so a new one can be used).
struct TunnelProblemView: View {
    @Environment(AppModel.self) private var model
    let problem: String

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "wifi.exclamationmark")
                .font(.system(size: 44))
                .foregroundStyle(Color.silver)
            Text("Can’t reach \(model.credentials?.guest?.ownerName ?? "the server")’s Flexarr")
                .font(.title2.weight(.semibold))
                .multilineTextAlignment(.center)
            Text(problem)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            HStack(spacing: 16) {
                Button("Try Again") { Task { await model.retryGuestTunnel() } }
                    .buttonStyle(.borderedProminent)
                Button("Leave", role: .destructive) { model.signOut() }
                    .buttonStyle(.bordered)
            }
        }
        .padding(40)
        .frame(maxWidth: 560)
    }
}
