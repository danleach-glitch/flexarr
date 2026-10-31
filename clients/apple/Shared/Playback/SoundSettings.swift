import Foundation
import FlexarrKit
#if canImport(AetherEngine)
import AetherEngine
#endif

/// How films and episodes sound on this device: the speakers, a lift for the dialogue and night mode. Kept on the
/// device (it's about the room), with the same choices and values as the web and Android apps. Settings › Playback and
/// the player's audio menu both change it; the engine shapes the sound (`AudioShaping`), and AirPlay leaves it out.
/// A stream the server converts has it shaped by the server (`PlaybackRequest.sound`).
struct SoundSettings: Codable, Equatable {
    var speakers: Speakers = .auto
    var dialogueBoost: DialogueBoost = .off
    var nightMode: NightMode = .off
    /// Minutes after midnight that a scheduled night mode starts and stops; it runs past midnight when start > end.
    var nightStart = 22 * 60
    var nightEnd = 7 * 60

    init() {}

    /// Field by field, so a setting this version doesn't know (or one a later version added) leaves the others as they were.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        func value<T: RawRepresentable>(_ key: CodingKeys) -> T? where T.RawValue == String {
            (try? c.decodeIfPresent(String.self, forKey: key)).flatMap { $0 }.flatMap(T.init(rawValue:))
        }
        func minutes(_ key: CodingKeys) -> Int? {
            (try? c.decodeIfPresent(Int.self, forKey: key)).flatMap { $0 }.flatMap { (0..<24 * 60).contains($0) ? $0 : nil }
        }
        speakers = value(.speakers) ?? .auto
        dialogueBoost = value(.dialogueBoost) ?? .off
        nightMode = value(.nightMode) ?? .off
        nightStart = minutes(.nightStart) ?? 22 * 60
        nightEnd = minutes(.nightEnd) ?? 7 * 60
    }

    enum Speakers: String, Codable, CaseIterable {
        case auto = "Auto", stereo = "Stereo", surround = "Surround"

        var label: String { self == .surround ? "5.1" : rawValue }
        var channels: Int? {
            switch self {
            case .auto: nil
            case .stereo: 2
            case .surround: 6
            }
        }
    }

    enum DialogueBoost: String, Codable, CaseIterable {
        case off = "Off", low = "Low", medium = "Medium", high = "High"

        /// The lift each strength gives the dialogue, the same in every app.
        var decibels: Double {
            switch self {
            case .off: 0
            case .low: 3
            case .medium: 6
            case .high: 9
            }
        }
    }

    enum NightMode: String, Codable, CaseIterable {
        case off = "Off", on = "On", scheduled = "Scheduled"
    }

    /// Whether night mode is on at `date`, with the schedule worked out.
    func isNight(at date: Date, calendar: Calendar = .current) -> Bool {
        switch nightMode {
        case .off: return false
        case .on: return true
        case .scheduled:
            let parts = calendar.dateComponents([.hour, .minute], from: date)
            let minute = (parts.hour ?? 0) * 60 + (parts.minute ?? 0)
            return nightStart <= nightEnd ? (nightStart..<nightEnd).contains(minute) : minute >= nightStart || minute < nightEnd
        }
    }

    /// Whether any of it asks for shaping, which costs passthrough (Atmos plays as its bed) while it's on.
    func isShaping(at date: Date) -> Bool {
        speakers.channels != nil || dialogueBoost != .off || isNight(at: date)
    }

    /// The night-mode hours as "22:00–07:00".
    var nightHours: String { "\(Self.clock(nightStart))–\(Self.clock(nightEnd))" }

    static func clock(_ minutes: Int) -> String { String(format: "%02d:%02d", minutes / 60, minutes % 60) }

    /// What the server is asked for, for a stream it converts (the engine isn't decoding it, so it can't shape it):
    /// nil when there's nothing to shape.
    func requested(at date: Date) -> PlaybackSound? {
        let night = isNight(at: date)
        guard dialogueBoost != .off || night else { return nil }
        return PlaybackSound(maxChannels: speakers.channels, dialogueBoostDb: dialogueBoost.decibels, night: night)
    }

    #if canImport(AetherEngine)
    /// What the engine is asked for at `date`, for a track of `sourceChannels`. A cap the track already fits under is
    /// left off: it would change nothing but still cost passthrough (5.1 for a 5.1 film, Stereo for a stereo one).
    func shaping(at date: Date, sourceChannels: Int?) -> AudioShaping {
        var cap = speakers.channels
        if let cap0 = cap, let sourceChannels, sourceChannels > 0, sourceChannels <= cap0 { cap = nil }
        return AudioShaping(maxChannels: cap, dialogueGainDB: dialogueBoost.decibels,
                            compressor: isNight(at: date) ? Self.night : nil)
    }

    /// Night mode, the same in every app and the server: everything above -40 dBFS compressed 8:1 and lifted 14 dB,
    /// so whispers come up and explosions come down far enough to hear on a phone's speaker. (The engine's own `.night`
    /// was too gentle to notice.)
    static let night = AudioShaping.Compressor(thresholdDB: -40, ratio: 8, makeupGainDB: 14,
                                               attackMilliseconds: 5, releaseMilliseconds: 250, kneeDB: 6)
    #endif

    // MARK: Kept on the device

    /// Posted when the settings change, for a player that's open.
    static let changed = Notification.Name("FlexarrSoundSettingsChanged")
    private static let key = "FlexarrSound"

    static var current: SoundSettings {
        get {
            guard let data = UserDefaults.standard.data(forKey: key),
                  let saved = try? JSONDecoder().decode(SoundSettings.self, from: data) else { return SoundSettings() }
            return saved
        }
        set {
            guard newValue != current, let data = try? JSONEncoder().encode(newValue) else { return }
            UserDefaults.standard.set(data, forKey: key)
            NotificationCenter.default.post(name: changed, object: nil)
        }
    }
}
