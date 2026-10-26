import FlexarrKit
import SwiftUI

struct PodcastSettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @State private var notifiesPodcasts = false
    @State private var notificationsRefused = false

    var body: some View {
        List {
            if let podcasts = model.podcastPreferences {
                Section {
                    Toggle("Auto-download new episodes", isOn: Binding(get: { podcasts.autoDownload }, set: { value in
                        Task {
                            await model.updatePodcastPreferences(.init(autoDownload: value))
                            await downloads.reconcilePodcasts()
                        }
                    }))
                    .settingsRow()
                    Stepper("Keep the newest \(podcasts.keepEpisodes)", value: Binding(get: { podcasts.keepEpisodes }, set: { value in
                        Task {
                            await model.updatePodcastPreferences(.init(keepEpisodes: value))
                            await downloads.reconcilePodcasts()
                        }
                    }), in: 1...50)
                    .disabled(!podcasts.autoDownload)
                    .listRowBackground(Color.seat)
                    Picker("Speed", selection: Binding(get: { podcasts.speed }, set: { value in
                        Task { await model.updatePodcastPreferences(.init(speed: value)) }
                    })) {
                        ForEach(MusicPlayer.speeds, id: \.self) { Text(speedLabel($0)).tag($0) }
                    }
                    .listRowBackground(Color.seat)
                } header: {
                    Text("Your profile, on every device")
                } footer: {
                    Text("Auto-download keeps the newest unplayed episodes of every show you follow, and deletes them once they’re played. Picking a speed in the player changes it here too.")
                }
            }

            Section {
                Toggle("Notify me of new episodes", isOn: Binding(get: { notifiesPodcasts }, set: { on in
                    Task {
                        if on, !(await PodcastNotifier.askIfNeeded()) {
                            notificationsRefused = true
                            return
                        }
                        PodcastNotifier.isEnabled = on
                        notifiesPodcasts = on
                    }
                }))
                .settingsRow()
            } header: {
                Text("This \(ThisDevice.name)")
            }
        }
        .settingsPage("Podcasts")
        .task {
            let allowed = await PodcastNotifier.isAllowed()
            notifiesPodcasts = PodcastNotifier.isEnabled && allowed
        }
        .alert("Notifications are off for Flexarr", isPresented: $notificationsRefused) {
            Button("Open Settings") { if let url = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(url) } }
            Button("Not now", role: .cancel) {}
        } message: {
            Text("Allow notifications for Flexarr in the Settings app to hear about new episodes.")
        }
    }
}
