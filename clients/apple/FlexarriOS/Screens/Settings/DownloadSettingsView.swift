import FlexarrKit
import SwiftUI

struct DownloadSettingsView: View {
    @Environment(DownloadStore.self) private var downloads
    @State private var confirmDeleteDownloads = false

    var body: some View {
        List {
            Section {
                Picker("Size", selection: Binding(get: { downloads.quality }, set: { downloads.quality = $0 })) {
                    Text("Automatic (720p)").tag(DownloadQuality.medium)
                    Text("Original").tag(DownloadQuality.original)
                    Text("Small (480p)").tag(DownloadQuality.small)
                }
                .listRowBackground(Color.seat)
            } header: {
                Text("Movies and episodes")
            } footer: {
                Text("Original keeps the full picture and sound but takes the most space. Albums, playlists and podcasts always come as their files are.")
            }

            Section {
                Toggle("Download the next episode", isOn: Binding(get: { downloads.downloadsNext }, set: { downloads.downloadsNext = $0 }))
                    .settingsRow()
                Toggle("Download the next book in a series", isOn: Binding(get: { downloads.downloadsNextBook }, set: { downloads.downloadsNextBook = $0 }))
                    .settingsRow()
                Toggle("Remove books once finished", isOn: Binding(get: { downloads.removesFinishedBooks }, set: { downloads.removesFinishedBooks = $0 }))
                    .settingsRow()
                Toggle("Automatic downloads on Wi-Fi only", isOn: Binding(get: { downloads.autoDownloadsOnWiFiOnly }, set: { downloads.autoDownloadsOnWiFiOnly = $0 }))
                    .settingsRow()
            } header: {
                Text("Automatic")
            } footer: {
                Text(downloads.autoDownloadsOnWiFiOnly
                    ? "Downloads this \(ThisDevice.name) starts by itself, like the next episode or new podcast episodes, wait for Wi-Fi and never use Low Data Mode. Ones you start yourself use mobile data too."
                    : "Downloads this \(ThisDevice.name) starts by itself, like the next episode or new podcast episodes, use mobile data too, but never Low Data Mode.")
            }

            Section {
                LabeledContent("Space used", value: downloads.bytesUsed == 0 ? "None" : ByteCountFormatter.string(fromByteCount: downloads.bytesUsed, countStyle: .file))
                    .listRowBackground(Color.seat)
                if !downloads.all.isEmpty {
                    Button("Delete all downloads", role: .destructive) { confirmDeleteDownloads = true }
                        .foregroundStyle(Color.danger)
                        .listRowBackground(Color.seat)
                }
            } header: {
                Text("Storage")
            } footer: {
                Text("Episodes are deleted a day after you finish them, and podcast episodes once they’re played.")
            }
        }
        .settingsPage("Downloads")
        .alert("Delete all downloads?", isPresented: $confirmDeleteDownloads) {
            Button("Delete all", role: .destructive) { downloads.deleteAll() }
            Button("Cancel", role: .cancel) {}
        }
    }
}
