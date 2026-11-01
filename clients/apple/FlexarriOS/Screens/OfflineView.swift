import SwiftUI

/// Home, Libraries and Search while the server can't be reached: one calm screen pointing at the downloads, which
/// play without it, rather than a spinner or an error. With nothing downloaded it just says so. The app swaps back by
/// itself once the server answers.
struct OfflineView: View {
    @Environment(DownloadStore.self) private var downloads
    /// The tab's title, kept so the tab doesn't lose its bearings.
    var title: String?
    let onDownloads: () -> Void

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "wifi.slash")
                .font(.system(size: 40))
                .foregroundStyle(Color.silver)
                .accessibilityHidden(true)
            Text("You’re offline")
                .font(.title2.weight(.semibold))
                .accessibilityAddTraits(.isHeader)
            if downloads.hasAnything {
                Text("Your downloads still play.")
                    .foregroundStyle(.secondary)
                Button("Go to Downloads", action: onDownloads)
                    .buttonStyle(ActionButtonStyle(prominent: true))
                    .padding(.top, 8)
            }
        }
        .multilineTextAlignment(.center)
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.room.ignoresSafeArea())
        .navigationTitle(title ?? "")
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("offline")
    }
}

/// The slim bar over every tab while offline, on a strip of its own so nothing scrolls by beside it.
struct OfflineBar: View {
    var body: some View {
        Label("Offline", systemImage: "wifi.slash")
            .font(.footnote.weight(.semibold))
            .foregroundStyle(Color.screen)
            .padding(.horizontal, 14)
            .padding(.vertical, 6)
            .background(Color.seat, in: Capsule())
            .padding(.vertical, 4)
            .frame(maxWidth: .infinity)
            .background(Color.room)
            .accessibilityIdentifier("offlineBar")
    }
}

extension DownloadStore {
    /// Something's downloaded for this profile: films, episodes, music, podcasts or books.
    var hasAnything: Bool {
        !(videoDownloads.isEmpty && musicCollections.isEmpty && podcastDownloads.isEmpty && bookDownloads.isEmpty)
    }
}
