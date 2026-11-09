import FlexarrKit
import SwiftUI

struct DownloadSettingsView: View {
    @Environment(DownloadStore.self) private var downloads
    @State private var confirmDeleteDownloads = false

    var body: some View {
        List {
            Section {
                Picker(t("Size"), selection: Binding(get: { downloads.quality }, set: { downloads.quality = $0 })) {
                    Text(t("Automatic (720p)")).tag(DownloadQuality.medium)
                    Text(t("Original")).tag(DownloadQuality.original)
                    Text(t("Small (480p)")).tag(DownloadQuality.small)
                }
                .listRowBackground(Color.seat)
            } header: {
                PageTopHeader(t("Movies and episodes"))
            } footer: {
                Text(t("Original keeps the full picture and sound but takes the most space. Albums, playlists and podcasts always come as their files are."))
            }

            Section {
                Toggle(t("Download the next episode"), isOn: Binding(get: { downloads.downloadsNext }, set: { downloads.downloadsNext = $0 }))
                    .settingsRow()
                Toggle(t("Download the next book in a series"), isOn: Binding(get: { downloads.downloadsNextBook }, set: { downloads.downloadsNextBook = $0 }))
                    .settingsRow()
                Toggle(t("Remove books once finished"), isOn: Binding(get: { downloads.removesFinishedBooks }, set: { downloads.removesFinishedBooks = $0 }))
                    .settingsRow()
                Toggle(t("Automatic downloads on Wi-Fi only"), isOn: Binding(get: { downloads.autoDownloadsOnWiFiOnly }, set: { downloads.autoDownloadsOnWiFiOnly = $0 }))
                    .settingsRow()
            } header: {
                Text(t(Words.automatic))
            } footer: {
                Text(downloads.autoDownloadsOnWiFiOnly
                    ? t("Downloads this {device} starts by itself, like the next episode or new podcast episodes, wait for Wi-Fi and never use Low Data Mode. Ones you start yourself use mobile data too.", ["device": ThisDevice.name])
                    : t("Downloads this {device} starts by itself, like the next episode or new podcast episodes, use mobile data too, but never Low Data Mode.", ["device": ThisDevice.name]))
            }

            Section {
                LabeledContent(t("Space used"), value: downloads.bytesUsed == 0 ? t("None") : formatBytes(downloads.bytesUsed))
                    .listRowBackground(Color.seat)
                if !downloads.all.isEmpty {
                    Button(t("Delete all downloads"), role: .destructive) { confirmDeleteDownloads = true }
                        .foregroundStyle(Color.danger)
                        .listRowBackground(Color.seat)
                }
            } header: {
                Text(t("Storage"))
            } footer: {
                Text(t("Episodes are deleted a day after you finish them, and podcast episodes once they’re played."))
            }
        }
        .settingsPage(t(Words.downloads))
        .alert(t("Delete all downloads?"), isPresented: $confirmDeleteDownloads) {
            Button(t("Delete all"), role: .destructive) { downloads.deleteAll() }
            Button(t(Words.cancel), role: .cancel) {}
        }
    }
}
