import SwiftUI

/// Home, Libraries and Search while the server can't be reached: one calm screen pointing at the downloads, which
/// play without it, rather than a spinner or an error. The app swaps back by itself once the server answers.
struct OfflineView: View {
    let onDownloads: () -> Void

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "wifi.slash")
                .font(.system(size: 40))
                .foregroundStyle(Color.silver)
            Text("You’re offline")
                .font(.title2.weight(.semibold))
            Text("Your downloads still play.")
                .foregroundStyle(.secondary)
            Button("Go to Downloads", action: onDownloads)
                .buttonStyle(ActionButtonStyle(prominent: true))
                .padding(.top, 8)
        }
        .multilineTextAlignment(.center)
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.room.ignoresSafeArea())
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("offline")
    }
}

/// The slim bar over every tab while offline.
struct OfflineBar: View {
    var body: some View {
        Label("Offline", systemImage: "wifi.slash")
            .font(.footnote.weight(.semibold))
            .foregroundStyle(Color.screen)
            .padding(.horizontal, 14)
            .padding(.vertical, 6)
            .background(.ultraThinMaterial, in: Capsule())
            .padding(.vertical, 4)
            .frame(maxWidth: .infinity)
            .accessibilityIdentifier("offlineBar")
    }
}
