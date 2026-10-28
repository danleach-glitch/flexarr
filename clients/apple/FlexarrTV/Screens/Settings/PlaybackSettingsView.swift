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
            } else {
                // The server's half arrives a moment later; this keeps the rows below from jumping up and back.
                SettingsGroupLabel("Your profile, on every device")
                ProgressView().padding(.leading, 32).frame(height: 78)
            }

            SettingsGroupLabel("This Apple TV")
            SettingsLinkRow(title: "Skip back and forward", symbol: "goforward.\(Int(skipSeconds))", value: "\(Int(skipSeconds)) seconds",
                            help: "How far a click on the left or right of the remote moves while something plays.") {
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
            SettingsRow.toggle("Music visualiser", isOn: visualiser, symbol: "waveform",
                               help: "Light in the cover's colours moves with the music behind Now Playing.") {
                visualiser.toggle()
                AudioSpectrum.isOn = visualiser
            }
        }
        .animation(.easeOut(duration: 0.2), value: playsFilesDirectly)
        .task {
            if let loaded = try? await model.client?.playbackPreferences() { preferences = loaded }
        }
    }

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

    /// Saved as soon as it changes, and put back if the server says no.
    private func save(_ changed: PlaybackPreferences, over current: PlaybackPreferences) {
        preferences = changed
        saveFailed = false
        Task {
            do {
                try await model.client?.setPlaybackPreferences(changed)
            } catch {
                preferences = current
                saveFailed = true
            }
        }
    }
}
