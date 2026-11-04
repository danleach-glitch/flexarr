import FlexarrKit
import SwiftUI

struct PlaybackSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var playsFilesDirectly = PlaybackController.usesEngine
    @State private var losslessAudio = PlaybackController.losslessAudio
    @State private var sound = SoundSettings.current
    @State private var skipSeconds = PlayerViewController.skipSeconds
    @State private var preferences: PlaybackPreferences?

    var body: some View {
        List {
            if let preferences {
                Section {
                    toggle(t(Words.autoPlayNext), \.autoPlayNext, preferences)
                    toggle(t("Skip intros"), \.skipIntros, preferences)
                    toggle(t("Skip recaps"), \.skipRecaps, preferences)
                    toggle(t(Words.skipCredits), \.skipCredits, preferences)
                    Picker(t("Subtitle size"), selection: Binding(
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
                    Picker(t("Subtitle position"), selection: Binding(
                        get: { preferences.subtitlePosition == .unknown ? .bottom : preferences.subtitlePosition ?? .bottom },
                        set: { position in
                            var changed = preferences
                            changed.subtitlePosition = position
                            save(changed, over: preferences)
                        }
                    )) {
                        ForEach(SubtitlePosition.allCases, id: \.self) { Text($0.label).tag($0) }
                    }
                    .listRowBackground(Color.seat)
                    Toggle(t("Subtitles in the black bars"), isOn: Binding(
                        get: { preferences.subtitlesInBlackBars ?? false },
                        set: { on in
                            var changed = preferences
                            changed.subtitlesInBlackBars = on
                            save(changed, over: preferences)
                        }
                    ))
                    .settingsRow()
                } header: {
                    PageTopHeader(t("Your profile, on every device"))
                } footer: {
                    Text(t("The next episode starts after a 10-second countdown. Subtitles that run early or late can be moved on Apple TV or the web. In the black bars puts subtitles below the picture of movies wider than the screen."))
                }
            }

            Section {
                Picker(t("Skip back and forward"), selection: $skipSeconds) {
                    ForEach([10.0, 15.0, 30.0], id: \.self) { seconds in
                        Text(t("{seconds} seconds", ["seconds": Int(seconds)])).tag(seconds)
                    }
                }
                .settingsRow()
                .onChange(of: skipSeconds) { _, seconds in UserDefaults.standard.set(seconds, forKey: "FlexarrSkipSeconds") }
                Toggle(t("Play files directly"), isOn: $playsFilesDirectly)
                    .settingsRow()
                    .onChange(of: playsFilesDirectly) { _, on in PlaybackController.usesEngine = on }
                if playsFilesDirectly {
                    Toggle(t("Lossless surround"), isOn: $losslessAudio)
                        .settingsRow()
                        .onChange(of: losslessAudio) { _, on in PlaybackController.losslessAudio = on }
                }
            } header: {
                Text(t("This {device}", ["device": ThisDevice.name]))
            } footer: {
                Text(playsFilesDirectly
                    ? t("Files play as they are, every format with HDR and Dolby Vision. Lossless surround sends TrueHD, DTS-HD and FLAC as multichannel PCM, for a receiver or eARC soundbar; off, they go as Dolby Digital Plus. Until you choose, it’s on when what’s connected takes it.")
                    : t("Off, the server sends a stream instead of the file itself. Downloads and remote viewing are unaffected."))
            }
            if playsFilesDirectly {
                Section {
                    Picker(t("Dialogue boost"), selection: soundSetting(\.dialogueBoost)) {
                        ForEach(SoundSettings.DialogueBoost.allCases, id: \.self) { Text(SoundNames.boost($0)).tag($0) }
                    }
                    .settingsRow()
                    Picker(t("Night mode"), selection: soundSetting(\.nightMode)) {
                        ForEach(SoundSettings.NightMode.allCases, id: \.self) { Text(SoundNames.night($0)).tag($0) }
                    }
                    .settingsRow()
                    if sound.nightMode == .scheduled {
                        DatePicker(t("From"), selection: minutes(\.nightStart), displayedComponents: .hourAndMinute)
                            .settingsRow()
                        DatePicker(t("Until"), selection: minutes(\.nightEnd), displayedComponents: .hourAndMinute)
                            .settingsRow()
                    }
                    Picker(t("Speakers"), selection: soundSetting(\.speakers)) {
                        ForEach(SoundSettings.Speakers.allCases, id: \.self) { Text($0.label).tag($0) }
                    }
                    .settingsRow()
                } header: {
                    Text(t("Sound"))
                } footer: {
                    Text(t("Dialogue boost makes voices easier to follow; night mode quietens loud scenes. While either is on, or the speakers are set, surround plays as PCM rather than passed through, and Atmos without its height."))
                }
            }
        }
        .settingsPage(t("Playback"))
        // The player's audio menu changes these too: this page follows, so it never saves an old value back.
        .onReceive(NotificationCenter.default.publisher(for: SoundSettings.changed)) { _ in sound = SoundSettings.current }
        .animation(.default, value: preferences == nil)
        .task {
            if let loaded = try? await model.client?.playbackPreferences() { preferences = loaded }
        }
    }

    /// One sound setting, saved on its own as soon as it changes, so a change made in the player since stays.
    private func soundSetting<Value>(_ key: WritableKeyPath<SoundSettings, Value>) -> Binding<Value> {
        Binding(get: { sound[keyPath: key] }, set: { SoundSettings.current[keyPath: key] = $0 })
    }

    /// A night-mode time (minutes after midnight) as the date a DatePicker shows: today, at that time.
    private func minutes(_ key: WritableKeyPath<SoundSettings, Int>) -> Binding<Date> {
        Binding(
            get: {
                let minutes = sound[keyPath: key]
                return Calendar.current.date(bySettingHour: minutes / 60, minute: minutes % 60, second: 0, of: Date()) ?? Date()
            },
            set: { date in
                let parts = Calendar.current.dateComponents([.hour, .minute], from: date)
                SoundSettings.current[keyPath: key] = (parts.hour ?? 0) * 60 + (parts.minute ?? 0)
            }
        )
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
                // Home's Continue watching row changes with it.
                if changed.listeningInContinue != current.listeningInContinue { model.invalidate() }
            } catch {
                preferences = current
            }
        }
    }
}

/// The names the sound settings show (their raw values are what's saved).
enum SoundNames {
    static func boost(_ boost: SoundSettings.DialogueBoost) -> String {
        switch boost {
        case .off: t("Off")
        case .low: t("Low")
        case .medium: t("Medium")
        case .high: t("High")
        }
    }

    static func night(_ night: SoundSettings.NightMode) -> String {
        switch night {
        case .off: t("Off")
        case .on: t("On")
        case .scheduled: t("On a schedule")
        }
    }
}
