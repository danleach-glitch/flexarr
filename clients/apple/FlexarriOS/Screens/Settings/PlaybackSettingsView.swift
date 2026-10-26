import FlexarrKit
import SwiftUI

struct PlaybackSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var playsFilesDirectly = PlaybackController.usesEngine
    @State private var losslessAudio = UserDefaults.standard.bool(forKey: "FlexarrEngineLossless")
    @State private var skipSeconds = PlayerViewController.skipSeconds
    @State private var preferences: PlaybackPreferences?

    var body: some View {
        List {
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
                            save(changed, over: preferences)
                        }
                    )) {
                        ForEach(SubtitleSize.allCases, id: \.self) { Text($0.label).tag($0) }
                    }
                    .listRowBackground(Color.seat)
                } header: {
                    Text("Your profile, on every device")
                } footer: {
                    Text("Auto-play counts down five seconds, then starts the next episode. Subtitles that run early or late can be moved on Apple TV or the web.")
                }
            }

            Section {
                Picker("Skip back and forward", selection: $skipSeconds) {
                    ForEach([10.0, 15.0, 30.0], id: \.self) { seconds in
                        Text("\(Int(seconds)) seconds").tag(seconds)
                    }
                }
                .settingsRow()
                .onChange(of: skipSeconds) { _, seconds in UserDefaults.standard.set(seconds, forKey: "FlexarrSkipSeconds") }
                Toggle("Play files directly", isOn: $playsFilesDirectly)
                    .settingsRow()
                    .onChange(of: playsFilesDirectly) { _, on in PlaybackController.usesEngine = on }
                if playsFilesDirectly {
                    Toggle("Lossless surround", isOn: $losslessAudio)
                        .settingsRow()
                        .onChange(of: losslessAudio) { _, on in UserDefaults.standard.set(on, forKey: "FlexarrEngineLossless") }
                }
            } header: {
                Text("This \(ThisDevice.name)")
            } footer: {
                Text(playsFilesDirectly
                    ? "Files play as they are, every format with HDR and Dolby Vision. Lossless surround sends TrueHD, DTS-HD and FLAC as multichannel PCM, for a receiver or eARC soundbar; off, they go as Dolby Digital Plus."
                    : "Off, the server sends a stream instead of the file itself. Downloads and remote viewing are unaffected.")
            }
        }
        .settingsPage("Playback")
        .animation(.default, value: preferences == nil)
        .task {
            if let loaded = try? await model.client?.playbackPreferences() { preferences = loaded }
        }
    }

    /// A preference that's saved as soon as it changes, and put back if the server says no.
    private func toggle(_ title: String, _ key: WritableKeyPath<PlaybackPreferences, Bool>, _ current: PlaybackPreferences) -> some View {
        Toggle(title, isOn: Binding(
            get: { current[keyPath: key] },
            set: { on in
                var changed = current
                changed[keyPath: key] = on
                save(changed, over: current)
            }
        ))
        .settingsRow()
    }

    private func save(_ changed: PlaybackPreferences, over current: PlaybackPreferences) {
        preferences = changed
        Task {
            do {
                try await model.client?.setPlaybackPreferences(changed)
            } catch {
                preferences = current
            }
        }
    }
}
