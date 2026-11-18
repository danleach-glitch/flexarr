import FlexarrKit
import SwiftUI

/// Home, Libraries and Search while the server can't be reached: one calm screen pointing at the downloads, which
/// play without it, rather than a spinner or an error. Offline here means the server didn't answer (the phone may well
/// be on Wi-Fi while the server restarts), so it's worded as the Apple TV words it, with Try again rather than waiting
/// for the next check. The app swaps back by itself once the server answers.
struct OfflineView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @State private var checking = false
    /// The tab's title, kept so the tab doesn't lose its bearings.
    var title: String?
    let onDownloads: () -> Void

    /// The server's name (a guest's host: "Dan’s Flexarr" when it has none), or "this Flexarr".
    private var server: ServerNaming {
        model.credentials?.isGuest == true
            ? model.guestServerNaming(model.credentials?.guest) : ServerNaming(name: model.serverName, owner: nil)
    }

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "wifi.slash")
                .font(.system(size: 40))
                .foregroundStyle(Color.silver)
                .accessibilityHidden(true)
            Text(t("{kind, select, named {Can’t reach {server}} owner {Can’t reach {owner}’s Flexarr} other {Can’t reach this Flexarr}}", server.values))
                .font(.title2.weight(.semibold))
                .accessibilityAddTraits(.isHeader)
            Text(t("{kind, select, named {Check this device’s internet, or that {server} is on.} owner {Check this device’s internet, or that {owner}’s Flexarr is on.} other {Check this device’s internet, or that its server is on.}}", server.values))
                .foregroundStyle(.secondary)
            // Stays pressable while it tries: a press meanwhile simply waits for the same check.
            Button {
                guard !checking else { return }
                Task {
                    checking = true
                    await model.checkConnection(ignoringNetwork: true)
                    checking = false
                }
            } label: {
                // The words stay (hidden) so the button keeps its size, and the spinner takes the words' colour: a
                // plain ProgressView is the default grey, unseen on the main button's light fill.
                Text(t("Try again"))
                    .opacity(checking ? 0 : 1)
                    .overlay { if checking { ProgressView().tint(Color.room) } }
            }
            .buttonStyle(ActionButtonStyle(prominent: true))
            .accessibilityLabel(t("Try again"))
            .accessibilityIdentifier("offlineTryAgain")
            .padding(.top, 8)
            if downloads.hasAnything {
                Text(t("Your downloads still play."))
                    .foregroundStyle(.secondary)
                    .padding(.top, 16)
                Button(t("Go to Downloads"), action: onDownloads)
                    .buttonStyle(ActionButtonStyle())
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
