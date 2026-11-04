import FlexarrKit
import SwiftUI

/// The profile's playback preferences (saved on the server, so they follow it to every device), then this Apple TV's
/// own: how far the remote skips, how files are played and whether music has a visualiser.
struct PlaybackSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var preferences: PlaybackPreferences?
    @State private var saveFailed = false
    @State private var skipSeconds = PlayerViewController.skipSeconds
    @State private var playsFilesDirectly = PlaybackController.usesEngine
    @State private var losslessAudio = PlaybackController.losslessAudio
    @State private var sound = SoundSettings.current
    @State private var visualiser = AudioSpectrum.isOn

    var body: some View {
        SettingsPage(title: t("Playback"), summary: saveFailed ? t("That change couldn't be saved on the server, so it's been put back.") : nil) {
            if let preferences {
                SettingsGroupLabel(model.currentProfile.map { t("{name}, on every device", ["name": $0.name]) } ?? t("Your profile, on every device"))
                SettingsRow.toggle(t(Words.autoPlayNext), isOn: preferences.autoPlayNext, symbol: "forward.end",
                                   help: t(Words.autoPlayNextHelp)) {
                    change(\.autoPlayNext)
                }
                SettingsRow.toggle(t("Skip intros"), isOn: preferences.skipIntros, symbol: "forward",
                                   help: t("Jumps past a show's opening titles by itself. Off, a Skip intro button shows instead.")) {
                    change(\.skipIntros)
                }
                SettingsRow.toggle(t("Skip recaps"), isOn: preferences.skipRecaps, symbol: "clock.arrow.circlepath",
                                   help: t("Jumps past the \u{201C}Previously on\u{201D} at the start of an episode.")) {
                    change(\.skipRecaps)
                }
                SettingsRow.toggle(t(Words.skipCredits), isOn: preferences.skipCredits, symbol: "text.append",
                                   help: t(Words.skipCreditsHelp)) {
                    change(\.skipCredits)
                }
                SettingsLinkRow(title: t("Subtitle size"), symbol: "captions.bubble", value: subtitleSize.label,
                                help: t("How big subtitles are. Subtitles that run early or late can be moved from the player's subtitles menu.")) {
                    SettingsChoicePage(title: t("Subtitle size"), summary: t("For this profile on every device."),
                                       choices: SubtitleSize.allCases, label: \.label, selection: subtitleSize) { size in
                        var changed = preferences
                        changed.subtitleSize = size
                        save(changed, over: preferences)
                    }
                }
                SettingsLinkRow(title: t("Subtitle position"), symbol: "arrow.up.and.down.text.horizontal", value: subtitlePosition.label,
                                help: t("Raise subtitles clear of text in the picture or the edge of the TV. Styled subtitles keep the places they were made for.")) {
                    SettingsChoicePage(title: t("Subtitle position"), summary: t("For this profile on every device."),
                                       choices: SubtitlePosition.allCases, label: \.label, selection: subtitlePosition) { position in
                        var changed = preferences
                        changed.subtitlePosition = position
                        save(changed, over: preferences)
                    }
                }
                SettingsRow.toggle(t("Subtitles in the black bars"), isOn: preferences.subtitlesInBlackBars ?? false, symbol: "rectangle.bottomthird.inset.filled",
                                   help: t("In movies wider than the TV, subtitles go in the black bar below the picture rather than over it.")) {
                    var changed = preferences
                    changed.subtitlesInBlackBars = !(preferences.subtitlesInBlackBars ?? false)
                    save(changed, over: preferences)
                }
                // Servers before 0.58 don't have it.
                if let listening = preferences.listeningInContinue {
                    SettingsRow.toggle(t("Books and podcasts in Continue watching"), isOn: listening, symbol: "headphones") {
                        var changed = preferences
                        changed.listeningInContinue = !listening
                        save(changed, over: preferences)
                    }
                }
            } else {
                // The server's half arrives a moment later; this keeps the rows below from jumping up and back.
                SettingsGroupLabel(t("Your profile, on every device"))
                ProgressView().padding(.leading, 32).frame(height: 78)
            }

            // How books play: the profile's, shared with podcasts on the other apps. With an audiobooks library only.
            if let listening = model.bookListening {
                SettingsGroupLabel(t(Words.audiobooks))
                SettingsLinkRow(title: t("Speed"), symbol: "gauge.with.dots.needle.50percent", value: BookSpeed.label(listening.speed),
                                help: t("A book with a speed of its own keeps it.")) {
                    SettingsChoicePage(title: t("Speed"), choices: BookSpeed.choices(with: listening.speed), label: BookSpeed.label,
                                       selection: listening.speed) { speed in saveListening(.init(speed: speed)) }
                }
                SettingsLinkRow(title: t("Skip back"), symbol: "gobackward", value: Self.seconds(listening.skipBackSeconds)) {
                    SettingsChoicePage(title: t("Skip back"), choices: steps(listening.skipBackSeconds), label: { Self.seconds($0) },
                                       selection: listening.skipBackSeconds) { seconds in saveListening(.init(skipBackSeconds: seconds)) }
                }
                SettingsLinkRow(title: t("Skip forward"), symbol: "goforward", value: Self.seconds(listening.skipForwardSeconds)) {
                    SettingsChoicePage(title: t("Skip forward"), choices: steps(listening.skipForwardSeconds), label: { Self.seconds($0) },
                                       selection: listening.skipForwardSeconds) { seconds in saveListening(.init(skipForwardSeconds: seconds)) }
                }
                SettingsRow.toggle(t("Smart rewind"), isOn: listening.smartRewind, symbol: "arrow.counterclockwise",
                                   help: t("After a pause, start a little earlier.")) {
                    saveListening(.init(smartRewind: !listening.smartRewind))
                }
            }

            SettingsGroupLabel(t("This Apple TV"))
            SettingsLinkRow(title: t("Skip back and forward"), symbol: "goforward.\(Int(skipSeconds))", value: Self.seconds(Int(skipSeconds)),
                            help: t("How far a click on the left or right of the remote moves movies and shows.")) {
                SettingsChoicePage(title: t("Skip back and forward"), summary: t("A click on the left or right of the remote's touch surface."),
                                   choices: [10.0, 15.0, 30.0], label: { Self.seconds(Int($0)) }, selection: skipSeconds) { seconds in
                    skipSeconds = seconds
                    UserDefaults.standard.set(seconds, forKey: "FlexarrSkipSeconds")
                }
            }
            SettingsRow.toggle(t("Play files directly"), isOn: playsFilesDirectly, symbol: "cpu",
                               help: playsFilesDirectly
                                   ? t("Files play on this Apple TV as they are, every format with HDR and Dolby Vision. The server only steps in for the few it can't play.")
                                   : t("The server sends a stream instead of the file itself, converting what this Apple TV can't play.")) {
                playsFilesDirectly.toggle()
                PlaybackController.usesEngine = playsFilesDirectly
            }
            if playsFilesDirectly {
                SettingsRow.toggle(t("Lossless surround"), isOn: losslessAudio, symbol: "hifispeaker.2",
                                   help: t("TrueHD, DTS-HD and FLAC go as multichannel PCM over HDMI, for a receiver or eARC soundbar that takes it. Off, they go as Dolby Digital Plus, which anything plays. Until you choose, it's on when the Apple TV is connected to something that takes it.")) {
                    losslessAudio.toggle()
                    PlaybackController.losslessAudio = losslessAudio
                }
            }
            if playsFilesDirectly {
                SettingsLinkRow(title: t("Dialogue boost"), symbol: "person.wave.2", value: Self.boostLabel(sound.dialogueBoost),
                                help: t("Makes voices easier to follow under music and effects.")) {
                    SettingsChoicePage(title: t("Dialogue boost"), summary: soundSummary, choices: SoundSettings.DialogueBoost.allCases,
                                       label: Self.boostLabel, selection: sound.dialogueBoost) { SoundSettings.current.dialogueBoost = $0 }
                }
                SettingsLinkRow(title: t("Night mode"), symbol: "moon", value: sound.nightMode == .scheduled ? sound.nightHours : Self.nightLabel(sound.nightMode),
                                help: t("Quietens loud scenes and lifts quiet ones.")) {
                    SettingsChoicePage(title: t("Night mode"), summary: soundSummary, choices: SoundSettings.NightMode.allCases,
                                       label: Self.nightLabel, selection: sound.nightMode) {
                        SoundSettings.current.nightMode = $0
                    }
                }
                if sound.nightMode == .scheduled {
                    SettingsLinkRow(title: t("Night mode from"), symbol: "moon.zzz", value: SoundSettings.clock(sound.nightStart)) {
                        SettingsChoicePage(title: t("Night mode from"), choices: Self.hours, label: SoundSettings.clock, selection: sound.nightStart) {
                            SoundSettings.current.nightStart = $0
                        }
                    }
                    SettingsLinkRow(title: t("Night mode until"), symbol: "sunrise", value: SoundSettings.clock(sound.nightEnd)) {
                        SettingsChoicePage(title: t("Night mode until"), choices: Self.hours, label: SoundSettings.clock, selection: sound.nightEnd) {
                            SoundSettings.current.nightEnd = $0
                        }
                    }
                }
                SettingsLinkRow(title: t("Speakers"), symbol: "hifispeaker", value: sound.speakers.label,
                                help: t("Auto plays every channel what's connected takes.")) {
                    SettingsChoicePage(title: t("Speakers"), summary: soundSummary, choices: SoundSettings.Speakers.allCases,
                                       label: \.label, selection: sound.speakers) { SoundSettings.current.speakers = $0 }
                }
            }
            SettingsRow.toggle(t("Music visualiser"), isOn: visualiser, symbol: "waveform",
                               help: t("Light in the cover's colours moves with the music behind Now Playing.")) {
                visualiser.toggle()
                AudioSpectrum.isOn = visualiser
            }
        }
        .animation(.easeOut(duration: 0.2), value: playsFilesDirectly)
        // The player's audio menu changes these too: this page follows them.
        .onReceive(NotificationCenter.default.publisher(for: SoundSettings.changed)) { _ in sound = SoundSettings.current }
        .task {
            if let loaded = try? await model.client?.playbackPreferences() { preferences = loaded }
        }
    }

    /// What shaping the sound costs, said where it's chosen.
    private var soundSummary: String {
        t("While any of these is on, surround plays as PCM rather than passed through, and Atmos without its height.")
    }

    private static func boostLabel(_ boost: SoundSettings.DialogueBoost) -> String {
        switch boost {
        case .off: t("Off")
        case .low: t("Low")
        case .medium: t("Medium")
        case .high: t("High")
        }
    }

    private static func nightLabel(_ mode: SoundSettings.NightMode) -> String {
        switch mode {
        case .off: t("Off")
        case .on: t("On")
        case .scheduled: t("On a schedule")
        }
    }
    /// Night mode's hours, on the hour.
    private static let hours = (0..<24).map { $0 * 60 }

    private var subtitleSize: SubtitleSize {
        guard let size = preferences?.subtitleSize, size != .unknown else { return .standard }
        return size
    }

    private var subtitlePosition: SubtitlePosition {
        guard let position = preferences?.subtitlePosition, position != .unknown else { return .bottom }
        return position
    }

    private func change(_ key: WritableKeyPath<PlaybackPreferences, Bool>) {
        guard let preferences else { return }
        var changed = preferences
        changed[keyPath: key].toggle()
        save(changed, over: preferences)
    }

    /// How books play, saved for the profile (and podcasts, which share it); the book playing takes it at once.
    private func saveListening(_ update: BookListeningUpdate) {
        saveFailed = false
        Task { saveFailed = !(await model.updateBookListening(update)) }
    }

    private static func seconds(_ count: Int) -> String {
        t("{count, plural, one {# second} other {# seconds}}", ["count": count])
    }

    private func steps(_ current: Int) -> [Int] {
        BookListening.skipSteps.contains(current) ? BookListening.skipSteps : (BookListening.skipSteps + [current]).sorted()
    }

    /// Saved as soon as it changes, and put back if the server says no.
    private func save(_ changed: PlaybackPreferences, over current: PlaybackPreferences) {
        preferences = changed
        saveFailed = false
        Task {
            do {
                try await model.client?.setPlaybackPreferences(changed)
                // Home's Continue watching row changes with it.
                if changed.listeningInContinue != current.listeningInContinue { model.invalidate() }
            } catch {
                preferences = current
                saveFailed = true
            }
        }
    }
}
