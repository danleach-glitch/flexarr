import SwiftUI

/// The server can't be reached (no network, or no answer within a few seconds). There's nothing to play without it on
/// Apple TV, so it says so straight away, tries again by itself, and goes on to Home once the server answers.
struct OfflineView: View {
    @Environment(AppModel.self) private var model
    @State private var checking = false

    var body: some View {
        VStack(spacing: 30) {
            Image(systemName: "wifi.exclamationmark")
                .font(.system(size: 80))
                .foregroundStyle(Color.silver)
            Text("Can’t reach your server")
                .font(.system(size: 52, weight: .bold))
                .foregroundStyle(Color.screen)
            Text("Check the network, or that Flexarr is running.")
                .font(.system(size: 30))
                .foregroundStyle(Color.usher)
            Button {
                Task {
                    checking = true
                    await model.checkConnection()
                    checking = false
                }
            } label: {
                if checking { ProgressView() } else { Text("Try again") }
            }
            .buttonStyle(ActionButtonStyle(prominent: true))
            .disabled(checking)
            .padding(.top, 10)
        }
        .multilineTextAlignment(.center)
        .padding(80)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
