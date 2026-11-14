import FlexarrKit
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
            Text(t("You’re offline"))
                .font(.title2.weight(.semibold))
                .accessibilityAddTraits(.isHeader)
            if downloads.hasAnything {
                Text(t("Your downloads still play."))
                    .foregroundStyle(.secondary)
                Button(t("Go to Downloads"), action: onDownloads)
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

/// The slim bar over every tab while offline, on a strip of its own so nothing scrolls by beside it. Where the top
/// of the screen is taken (a wide iPad's tab bar and back button) it `floats` instead: just the pill, over the bottom
/// of the page with the mini player, so it can't cover a control.
struct OfflineBar: View {
    var floats = false

    var body: some View {
        Label(t("Offline"), systemImage: "wifi.slash")
            .font(.footnote.weight(.semibold))
            .foregroundStyle(Color.screen)
            .padding(.horizontal, 14)
            .padding(.vertical, 6)
            .background {
                // Lifted off the page like the mini player when there's no strip behind it.
                Capsule().fill(Color.seat).shadow(color: .black.opacity(floats ? 0.35 : 0), radius: 12, y: 4)
            }
            .padding(.vertical, 4)
            .frame(maxWidth: floats ? nil : .infinity)
            .background(floats ? Color.clear : Color.room)
            .accessibilityIdentifier("offlineBar")
    }
}

extension DownloadStore {
    /// Something's downloaded for this profile: films, episodes, music, podcasts or books.
    var hasAnything: Bool {
        !(videoDownloads.isEmpty && musicCollections.isEmpty && podcastDownloads.isEmpty && bookDownloads.isEmpty)
    }
}
