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
                Text("Original keeps the full picture and sound but takes the most space. Once an episode is downloaded, the next one downloads too, on Wi-Fi. Episodes are deleted a day after you finish them.")
            }

            Section("Server") {
                row("Address", model.credentials?.address.absoluteString ?? "")
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
        do {
            status = try await client.systemStatus()
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
        if let loaded = await loadedPreferences { preferences = loaded }
    }
}
