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
        SettingsPage(title: "Playback", summary: saveFailed ? "That change couldn't be saved on the server, so it's been put back." : nil) {
            if let preferences {
                SettingsGroupLabel(model.currentProfile.map { "\($0.name), on every device" } ?? "Your profile, on every device")
                SettingsRow.toggle("Auto-play next episode", isOn: preferences.autoPlayNext, symbol: "forward.end",
                                   help: "When an episode ends, the next one starts after a five-second countdown.") {
                    change(\.autoPlayNext)
                }
                SettingsRow.toggle("Skip intros", isOn: preferences.skipIntros, symbol: "forward",
                                   help: "Jumps past a show's opening titles by itself. Off, a Skip Intro button shows instead.") {
                    change(\.skipIntros)
                }
                SettingsRow.toggle("Skip recaps", isOn: preferences.skipRecaps, symbol: "clock.arrow.circlepath",
                                   help: "Jumps past the \u{201C}Previously on\u{201D} at the start of an episode.") {
                    change(\.skipRecaps)
                }
                SettingsRow.toggle("Skip credits", isOn: preferences.skipCredits, symbol: "text.append",
                                   help: "Goes on to the next episode when the credits start, rather than when they end.") {
                    change(\.skipCredits)
                }
                SettingsLinkRow(title: "Subtitle size", symbol: "captions.bubble", value: subtitleSize.label,
                                help: "How big subtitles are. Subtitles that run early or late can be moved from the player's subtitles menu.") {
                    SettingsChoicePage(title: "Subtitle size", summary: "For this profile on every device.",
                                       choices: SubtitleSize.allCases, label: \.label, selection: subtitleSize) { size in
                        var changed = preferences
                        changed.subtitleSize = size
                        save(changed, over: preferences)
                    }
                }
                SettingsLinkRow(title: "Subtitle position", symbol: "arrow.up.and.down.text.horizontal", value: subtitlePosition.label,
                                help: "Raise subtitles clear of text in the picture or the edge of the TV. Styled subtitles keep the places they were made for.") {
                    SettingsChoicePage(title: "Subtitle position", summary: "For this profile on every device.",
                                       choices: SubtitlePosition.allCases, label: \.label, selection: subtitlePosition) { position in
                        var changed = preferences
                        changed.subtitlePosition = position
                        save(changed, over: preferences)
                    }
                }
                SettingsRow.toggle("Subtitles in the black bars", isOn: preferences.subtitlesInBlackBars ?? false, symbol: "rectangle.bottomthird.inset.filled",
                                   help: "In films wider than the TV, subtitles go in the black bar below the picture rather than over it.") {
                    var changed = preferences
                    changed.subtitlesInBlackBars = !(preferences.subtitlesInBlackBars ?? false)
                    save(changed, over: preferences)
                }
                // Servers before 0.58 don't have it.
                if let listening = preferences.listeningInContinue {
                    SettingsRow.toggle("Books and podcasts in Continue watching", isOn: listening, symbol: "headphones") {
                        var changed = preferences
                        changed.listeningInContinue = !listening
                        save(changed, over: preferences)
                    }
                }
            } else {
                // The server's half arrives a moment later; this keeps the rows below from jumping up and back.
                SettingsGroupLabel("Your profile, on every device")
                ProgressView().padding(.leading, 32).frame(height: 78)
            }

            // How books play: the profile's, shared with podcasts on the other apps. With an audiobooks library only.
            if let listening = model.bookListening {
                SettingsGroupLabel("Audiobooks")
                SettingsLinkRow(title: "Speed", symbol: "gauge.with.dots.needle.50percent", value: BookSpeed.label(listening.speed),
                                help: "A book with a speed of its own keeps it.") {
                    SettingsChoicePage(title: "Speed", choices: BookSpeed.choices(with: listening.speed), label: BookSpeed.label,
                                       selection: listening.speed) { speed in saveListening(.init(speed: speed)) }
                }
                SettingsLinkRow(title: "Skip back", symbol: "gobackward", value: "\(listening.skipBackSeconds) seconds") {
                    SettingsChoicePage(title: "Skip back", choices: steps(listening.skipBackSeconds), label: { "\($0) seconds" },
                                       selection: listening.skipBackSeconds) { seconds in saveListening(.init(skipBackSeconds: seconds)) }
                }
                SettingsLinkRow(title: "Skip forward", symbol: "goforward", value: "\(listening.skipForwardSeconds) seconds") {
                    SettingsChoicePage(title: "Skip forward", choices: steps(listening.skipForwardSeconds), label: { "\($0) seconds" },
                                       selection: listening.skipForwardSeconds) { seconds in saveListening(.init(skipForwardSeconds: seconds)) }
                }
                SettingsRow.toggle("Smart rewind", isOn: listening.smartRewind, symbol: "arrow.counterclockwise",
                                   help: "After a pause, start a little earlier.") {
                    saveListening(.init(smartRewind: !listening.smartRewind))
                }
            }

            SettingsGroupLabel("This Apple TV")
            SettingsLinkRow(title: "Skip back and forward", symbol: "goforward.\(Int(skipSeconds))", value: "\(Int(skipSeconds)) seconds",
                            help: "How far a click on the left or right of the remote moves films and shows.") {
                SettingsChoicePage(title: "Skip back and forward", summary: "A click on the left or right of the remote's touch surface.",
                                   choices: [10.0, 15.0, 30.0], label: { "\(Int($0)) seconds" }, selection: skipSeconds) { seconds in
                    skipSeconds = seconds
                    UserDefaults.standard.set(seconds, forKey: "FlexarrSkipSeconds")
                }
            }
            SettingsRow.toggle("Play files directly", isOn: playsFilesDirectly, symbol: "cpu",
                               help: playsFilesDirectly
                                   ? "Files play on this Apple TV as they are, every format with HDR and Dolby Vision. The server only steps in for the few it can't play."
                                   : "The server sends a stream instead of the file itself, converting what this Apple TV can't play.") {
                playsFilesDirectly.toggle()
                PlaybackController.usesEngine = playsFilesDirectly
            }
            if playsFilesDirectly {
                SettingsRow.toggle("Lossless surround", isOn: losslessAudio, symbol: "hifispeaker.2",
                                   help: "TrueHD, DTS-HD and FLAC go as multichannel PCM over HDMI, for a receiver or eARC soundbar that takes it. Off, they go as Dolby Digital Plus, which anything plays. Until you choose, it's on when the Apple TV is connected to something that takes it.") {
                    losslessAudio.toggle()
                    PlaybackController.losslessAudio = losslessAudio
                }
            }
            if playsFilesDirectly {
                SettingsLinkRow(title: "Dialogue boost", symbol: "person.wave.2", value: sound.dialogueBoost.rawValue,
                                help: "Makes voices easier to follow under music and effects.") {
                    SettingsChoicePage(title: "Dialogue boost", summary: soundSummary, choices: SoundSettings.DialogueBoost.allCases,
                                       label: \.rawValue, selection: sound.dialogueBoost) { SoundSettings.current.dialogueBoost = $0 }
                }
                SettingsLinkRow(title: "Night mode", symbol: "moon", value: sound.nightMode == .scheduled ? sound.nightHours : sound.nightMode.rawValue,
                                help: "Quietens loud scenes and lifts quiet ones.") {
                    SettingsChoicePage(title: "Night mode", summary: soundSummary, choices: SoundSettings.NightMode.allCases,
                                       label: { $0 == .scheduled ? "On a schedule" : $0.rawValue }, selection: sound.nightMode) {
                        SoundSettings.current.nightMode = $0
                    }
                }
                if sound.nightMode == .scheduled {
                    SettingsLinkRow(title: "Night mode from", symbol: "moon.zzz", value: SoundSettings.clock(sound.nightStart)) {
                        SettingsChoicePage(title: "Night mode from", choices: Self.hours, label: SoundSettings.clock, selection: sound.nightStart) {
                            SoundSettings.current.nightStart = $0
                        }
                    }
                    SettingsLinkRow(title: "Night mode until", symbol: "sunrise", value: SoundSettings.clock(sound.nightEnd)) {
                        SettingsChoicePage(title: "Night mode until", choices: Self.hours, label: SoundSettings.clock, selection: sound.nightEnd) {
                            SoundSettings.current.nightEnd = $0
                        }
                    }
                }
                SettingsLinkRow(title: "Speakers", symbol: "hifispeaker", value: sound.speakers.label,
                                help: "Auto plays every channel what's connected takes.") {
                    SettingsChoicePage(title: "Speakers", summary: soundSummary, choices: SoundSettings.Speakers.allCases,
                                       label: \.label, selection: sound.speakers) { SoundSettings.current.speakers = $0 }
                }
            }
            SettingsRow.toggle("Music visualiser", isOn: visualiser, symbol: "waveform",
                               help: "Light in the cover's colours moves with the music behind Now Playing.") {
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
    private let soundSummary = "While any of these is on, surround plays as PCM rather than passed through, and Atmos without its height."
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
