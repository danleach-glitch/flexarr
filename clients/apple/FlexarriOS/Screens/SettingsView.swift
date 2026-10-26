import FlexarrKit
import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @State private var confirmDeleteDownloads = false
    @State private var playsFilesDirectly = PlaybackController.usesEngine
    @State private var losslessAudio = UserDefaults.standard.bool(forKey: "FlexarrEngineLossless")
    @State private var skipSeconds = PlayerViewController.skipSeconds
    @State private var status: SystemStatus?
    @State private var error: String?
    @State private var confirmSignOut = false
    @State private var choosingProfile = false
    @State private var preferences: PlaybackPreferences?
    @State private var devices: [Device] = []
    @State private var siriDevice = AppModel.siriDevice ?? ""
    @State private var notifiesPodcasts = false
    @State private var notificationsRefused = false

    var body: some View {
        List {
            if let current = model.currentProfile {
                Section("Watching as") {
                    HStack(spacing: 14) {
                        ProfileAvatar(profile: current, size: 44)
                        Text(current.name).font(.body.weight(.semibold))
                        Spacer()
                        if model.profiles.count > 1 {
                            Button("Switch") { choosingProfile = true }
                                .font(.subheadline.weight(.semibold))
                        }
                    }
                    .padding(.vertical, 4)
                    .listRowBackground(Color.seat)
                }
            }

            Section {
                Picker("Skip back and forward", selection: $skipSeconds) {
                    ForEach([10.0, 15.0, 30.0], id: \.self) { seconds in
                        Text("\(Int(seconds)) seconds").tag(seconds)
                    }
                }
                .tint(.silver)
                .listRowBackground(Color.seat)
                .onChange(of: skipSeconds) { _, seconds in UserDefaults.standard.set(seconds, forKey: "FlexarrSkipSeconds") }
                Toggle("Play files directly", isOn: $playsFilesDirectly)
                    .tint(.silver)
                    .listRowBackground(Color.seat)
                    .onChange(of: playsFilesDirectly) { _, on in PlaybackController.usesEngine = on }
                if playsFilesDirectly {
                    Toggle("Lossless surround", isOn: $losslessAudio)
                        .tint(.silver)
                        .listRowBackground(Color.seat)
                        .onChange(of: losslessAudio) { _, on in UserDefaults.standard.set(on, forKey: "FlexarrEngineLossless") }
                }
            } header: {
                Text("Playback")
            } footer: {
                Text(playsFilesDirectly
                    ? "Files play on this \(ThisDevice.name) as they are, every format with HDR and Dolby Vision. Lossless surround sends TrueHD, DTS-HD and FLAC as multichannel PCM, for a receiver or eARC soundbar over AirPlay or HDMI; off, they go as Dolby Digital Plus."
                    : "Off, the server sends a stream instead of the file itself. Downloads and remote viewing are unaffected.")
            }

            if let preferences {
                Section {
                    toggle("Auto-play the next episode", \.autoPlayNext, preferences)
                    toggle("Skip intros", \.skipIntros, preferences)
                    toggle("Skip recaps", \.skipRecaps, preferences)
                    toggle("Skip credits", \.skipCredits, preferences)
                    Picker("Subtitle size", selection: Binding(
                        get: { preferences.subtitleSize == .unknown ? .standard : preferences.subtitleSize ?? .standard },
                        set: { size in
                            var changed = preferences
                            changed.subtitleSize = size
                            self.preferences = changed
                            Task {
                                do {
                                    try await model.client?.setPlaybackPreferences(changed)
                                } catch {
                                    self.preferences = preferences
                                }
                            }
                        }
                    )) {
                        ForEach(SubtitleSize.allCases, id: \.self) { Text($0.label).tag($0) }
                    }
                    .listRowBackground(Color.seat)
                } header: {
                    Text("While watching")
                } footer: {
                    Text("Auto-play counts down five seconds, then starts the next episode. Skip credits moves on as the credits start. Each profile has its own, on every device. Subtitles that run early or late can be moved on Apple TV or the web.")
                }
            }

            if model.podcastStatus?.available == true, let podcasts = model.podcastPreferences {
                Section {
                    Toggle("Auto-download new episodes", isOn: Binding(get: { podcasts.autoDownload }, set: { value in
                        Task {
                            await model.updatePodcastPreferences(.init(autoDownload: value))
                            await downloads.reconcilePodcasts()
                        }
                    }))
                    .tint(.silver)
                    .listRowBackground(Color.seat)
                    Stepper("Keep the newest \(podcasts.keepEpisodes)", value: Binding(get: { podcasts.keepEpisodes }, set: { value in
                        Task {
                            await model.updatePodcastPreferences(.init(keepEpisodes: value))
                            await downloads.reconcilePodcasts()
                        }
                    }), in: 1...50)
                    .disabled(!podcasts.autoDownload)
                    .listRowBackground(Color.seat)
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
                    .tint(.silver)
                    .listRowBackground(Color.seat)
                    Picker("Speed", selection: Binding(get: { podcasts.speed }, set: { value in
                        Task { await model.updatePodcastPreferences(.init(speed: value)) }
                    })) {
                        ForEach(MusicPlayer.speeds, id: \.self) { Text(speedLabel($0)).tag($0) }
                    }
                    .listRowBackground(Color.seat)
                } header: {
                    Text("Podcasts")
                } footer: {
                    Text("Auto-download keeps the newest unplayed episodes of every show you follow on this \(ThisDevice.name) (on Wi-Fi only, unless that's off under Downloads), and deletes them once they’re played. These settings are your profile’s, on every device; the speed also changes when you pick one in the player. Notifications are this \(ThisDevice.name)’s own.")
                }
            }

            Section {
                Picker("Size", selection: Binding(get: { downloads.quality }, set: { downloads.quality = $0 })) {
                    Text("Automatic (720p)").tag(DownloadQuality.medium)
                    Text("Original").tag(DownloadQuality.original)
                    Text("Small (480p)").tag(DownloadQuality.small)
                }
                .listRowBackground(Color.seat)
                Toggle("Download the next episode", isOn: Binding(get: { downloads.downloadsNext }, set: { downloads.downloadsNext = $0 }))
                    .tint(.silver)
                    .listRowBackground(Color.seat)
                Toggle("Automatic downloads on Wi-Fi only", isOn: Binding(get: { downloads.autoDownloadsOnWiFiOnly }, set: { downloads.autoDownloadsOnWiFiOnly = $0 }))
                    .tint(.silver)
                    .listRowBackground(Color.seat)
                LabeledContent("Space used", value: downloads.bytesUsed == 0 ? "None" : ByteCountFormatter.string(fromByteCount: downloads.bytesUsed, countStyle: .file))
                    .listRowBackground(Color.seat)
                if !downloads.all.isEmpty {
                    Button("Delete all downloads", role: .destructive) { confirmDeleteDownloads = true }
                        .foregroundStyle(Color.danger)
                        .listRowBackground(Color.seat)
                }
            } header: {
                Text("Downloads")
            } footer: {
                Text("Original keeps the full picture and sound but takes the most space. Once an episode is downloaded, the next one downloads too. Downloads this \(ThisDevice.name) makes by itself (the next episode, new podcast episodes) wait for Wi-Fi unless Wi-Fi only is off, and never use Low Data Mode; ones you start yourself use mobile data too. Episodes are deleted a day after you finish them. Albums, playlists and podcasts always come as their files are.")
            }

            Section {
                Picker("Siri plays on", selection: $siriDevice) {
                    Text("This \(ThisDevice.name)").tag("")
                    ForEach(devices.filter { !$0.isThisOne }) { device in
                        Text(device.name).tag(device.id)
                    }
                    if !siriDevice.isEmpty, !devices.contains(where: { $0.id == siriDevice }) {
                        Text(AppModel.siriDeviceName ?? "A device no longer signed in").tag(siriDevice)
                    }
                }
                .tint(.silver)
                .listRowBackground(Color.seat)
                .onChange(of: siriDevice) { _, id in
                    AppModel.siriDevice = id.isEmpty ? nil : id
                    AppModel.siriDeviceName = devices.first { $0.id == id }?.name
                }
            } header: {
                Text("Siri")
            } footer: {
                Text("Say \u{201C}Play the next episode of Curb Your Enthusiasm in Flexarr\u{201D}, \u{201C}Continue watching in Flexarr\u{201D} or \u{201C}Pause Flexarr\u{201D}. Shows play from the episode you're up to. Choose an Apple TV here and Siri plays there (Flexarr needs to be open on it); you can also name one as you ask.")
            }

            Section {
                row("Address", model.credentials?.address.absoluteString ?? "")
                if let remote = model.credentials?.remoteAddress {
                    row("Away from home", remote.absoluteString)
                    row("Connected", model.client?.addresses.isAway == true ? "Away, over Tailscale" : "At home")
                }
                if let status {
                    row("Version", ServerVersion.short(status.version))
                    row("Running on", status.osDescription)
                    row("Up since", status.startTime.formatted(date: .abbreviated, time: .shortened))
                } else if let error {
                    row("Status", error)
                } else {
                    row("Status", "Checking")
                }
                row("Libraries", model.libraries.map(\.name).joined(separator: ", "))
            } header: {
                Text("Server")
            } footer: {
                Text(model.credentials?.remoteAddress == nil
                     ? "To use Flexarr away from home, turn on remote access with Tailscale in Settings › Remote access on the web; this \(ThisDevice.name) picks the address up by itself."
                     : "Away from home, this \(ThisDevice.name) reaches the server over Tailscale when it can't be reached at home. Tailscale needs to be connected on this \(ThisDevice.name).")
            }

            Section {
                Button(role: .destructive) { confirmSignOut = true } label: {
                    Label("Sign out", systemImage: "rectangle.portrait.and.arrow.right")
                        .foregroundStyle(Color.danger)
                }
                .listRowBackground(Color.seat)
            } footer: {
                Text("Flexarr for \(ThisDevice.name) \(Bundle.main.shortVersion)")
            }
        }
        .scrollContentBackground(.hidden)
        .background(RoomBackground(ambient: nil))
        .navigationTitle("Settings")
        .task { await load() }
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
        .refreshable { await load() }
        .confirmationDialog("Delete all downloads?", isPresented: $confirmDeleteDownloads, titleVisibility: .visible) {
            Button("Delete all", role: .destructive) { downloads.deleteAll() }
            Button("Cancel", role: .cancel) {}
        }
        .confirmationDialog("Sign out of this server?", isPresented: $confirmSignOut, titleVisibility: .visible) {
            Button("Sign out", role: .destructive) { model.signOut() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This \(ThisDevice.name) will need to connect again to use the server, and its downloads will be deleted.")
        }
        .sheet(isPresented: $choosingProfile) {
            ProfilePickerView(marksCurrent: true)
                .presentationDetents([.medium, .large])
                .presentationBackground(Color.room)
        }
    }

    /// A preference that's saved as soon as it changes, and put back if the server says no.
    private func toggle(_ title: String, _ key: WritableKeyPath<PlaybackPreferences, Bool>, _ current: PlaybackPreferences) -> some View {
        Toggle(title, isOn: Binding(
            get: { current[keyPath: key] },
            set: { on in
                var changed = current
                changed[keyPath: key] = on
                preferences = changed
                Task {
                    do {
                        try await model.client?.setPlaybackPreferences(changed)
                    } catch {
                        preferences = current
                    }
                }
            }
        ))
        .tint(.silver)
        .listRowBackground(Color.seat)
    }

    private func row(_ label: String, _ value: String) -> some View {
        LabeledContent(label) {
            Text(value)
                .foregroundStyle(Color.screen)
                .multilineTextAlignment(.trailing)
                .lineLimit(2)
        }
        .listRowBackground(Color.seat)
    }

    private func load() async {
        guard let client = model.client else { return }
        async let loadedPreferences = try? client.playbackPreferences()
        async let loadedDevices = try? client.devices()
        do {
            status = try await client.systemStatus()
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
        if let loaded = await loadedPreferences { preferences = loaded }
        if let loaded = await loadedDevices { devices = loaded }
    }
}
