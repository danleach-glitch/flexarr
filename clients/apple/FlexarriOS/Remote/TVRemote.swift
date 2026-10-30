import FlexarrKit
import Foundation
import Observation

/// The TVs in the house playing something, for the Playing on TV bar and the remote: kept up to date by the command
/// stream's now-playing events (`AppModel.onNowPlaying`), with the device list asked for now and then in case the
/// stream isn't there (an API-key sign-in, an older server). Commands change what's shown at once, as asked, and the
/// TV's own reports put it right if it didn't happen.
@MainActor
@Observable
final class TVRemote {
    /// Every device signed in, as last heard.
    private var devices: [Device] = []
    /// The device the remote is on; nil: the one playing that was used last.
    var selectedId: String?
    /// The remote is open: the list is asked for more often.
    var isOpen = false
    /// Why the last command didn't work, to show for a moment.
    private(set) var problem: String?

    @ObservationIgnored private var client: FlexarrClient?
    /// After a command, reports already on their way still say how it was: they're held back for a moment, then the
    /// list is asked for again.
    @ObservationIgnored private var holdUntil: [String: Date] = [:]
    @ObservationIgnored private var problemTask: Task<Void, Never>?

    /// A playing TV says where it is every ten seconds; this long without a word and it's gone (closed, asleep).
    private static let silence: TimeInterval = 45
    private static let hold: TimeInterval = 1.5

    /// Other devices playing something (not phones: a phone isn't a TV to control), the one used last first.
    var playing: [Device] {
        let now = Date()
        return devices.filter { device in
            guard !device.isThisOne, let nowPlaying = device.nowPlaying, device.deviceType != .iPhone, device.deviceType != .androidPhone else { return false }
            // Paused for a long while is left to the server, which stops listing it after a quarter of an hour.
            guard !nowPlaying.paused, let reportedAt = nowPlaying.reportedAt else { return true }
            return now.timeIntervalSince(reportedAt) < Self.silence
        }
        .sorted { $0.lastUsedAt > $1.lastUsedAt }
    }

    /// The device the remote is on.
    var current: Device? {
        let playing = playing
        return playing.first { $0.id == selectedId } ?? playing.first
    }

    /// Follows the sign-in: another server or profile starts afresh.
    func use(_ client: FlexarrClient?) {
        guard client?.baseURL != self.client?.baseURL || client?.apiKey != self.client?.apiKey else {
            self.client = client
            return
        }
        self.client = client
        devices = []
        selectedId = nil
    }

    func refresh() async {
        guard let client, let list = try? await client.devices() else { return }
        let now = Date()
        // What a command just changed stays as asked until the hold is over.
        devices = list.map { device in
            if let until = holdUntil[device.id], until > now, let shown = devices.first(where: { $0.id == device.id }) { return shown }
            return device
        }
    }

    /// The command stream says what a device is playing now.
    func received(_ deviceId: String, _ nowPlaying: DeviceNowPlaying?) {
        if let until = holdUntil[deviceId], until > Date() { return }
        if let index = devices.firstIndex(where: { $0.id == deviceId }) {
            devices[index].nowPlaying = nowPlaying
        } else {
            Task { await refresh() } // a device this phone hasn't heard of yet
        }
    }

    /// Asks for the list while the app is up: often while the remote is open, now and then otherwise (the stream
    /// says as things change; this catches what it can't).
    func follow() async {
        while !Task.isCancelled {
            await refresh()
            try? await Task.sleep(for: .seconds(isOpen ? 5 : 20))
        }
    }

    // MARK: Commands

    func togglePlayPause() {
        guard let device = current, let nowPlaying = device.nowPlaying else { return }
        let pausing = !nowPlaying.paused
        show(device.id) { $0.moved(paused: pausing) }
        send(to: device) { client in _ = try await client.control(device: device.id, pausing ? .pause : .resume) }
    }

    func seek(to seconds: Double) {
        guard let device = current, device.takes(.seek) else { return }
        show(device.id) { $0.moved(to: seconds) }
        send(to: device) { client in try await client.seek(device: device.id, to: seconds) }
    }

    func skip(by seconds: Double) {
        guard let device = current, device.takes(.skip), let nowPlaying = device.nowPlaying else { return }
        show(device.id) { $0.moved(to: nowPlaying.position(at: Date()) + seconds) }
        send(to: device) { client in try await client.skip(device: device.id, by: seconds) }
    }

    /// Next, previous or skipping the intro: the TV says what came of it.
    func control(_ action: DeviceControl) {
        guard let device = current, device.takes(action.kind) else { return }
        if action == .skipSegment { show(device.id) { $0.with(prompt: .some(nil)) } }
        send(to: device) { client in _ = try await client.control(device: device.id, action) }
    }

    /// Stops playing on the TV; the bar goes.
    func stop() {
        guard let device = current else { return }
        if let index = devices.firstIndex(where: { $0.id == device.id }) { devices[index].nowPlaying = nil }
        holdUntil[device.id] = Date().addingTimeInterval(Self.hold)
        send(to: device) { client in _ = try await client.control(device: device.id, .stop) }
    }

    func setAudio(_ track: PlayerTrack) {
        guard let device = current, device.takes(.audioTrack) else { return }
        show(device.id) { $0.with(tracks: $0.tracks.map { PlayerTracks(audio: $0.audio.selecting(track.id), subtitles: $0.subtitles) }) }
        send(to: device) { client in try await client.setAudioTrack(device: device.id, track.id) }
    }

    /// Subtitles on (a track) or off (nil).
    func setSubtitles(_ track: PlayerTrack?) {
        guard let device = current, device.takes(.subtitleTrack) else { return }
        show(device.id) { $0.with(tracks: $0.tracks.map { PlayerTracks(audio: $0.audio, subtitles: $0.subtitles.selecting(track?.id)) }) }
        send(to: device) { client in try await client.setSubtitleTrack(device: device.id, track?.id) }
    }

    func setRate(_ rate: Double) {
        guard let device = current, device.takes(.rate) else { return }
        show(device.id) { $0.with(rate: rate) }
        send(to: device) { client in try await client.setRate(device: device.id, rate) }
    }

    func setSound(_ sound: DeviceSound) {
        guard let device = current else { return }
        show(device.id) { playing in
            let before = playing.sound
            return playing.with(sound: DeviceSound(dialogueBoost: sound.dialogueBoost ?? before?.dialogueBoost, nightMode: sound.nightMode ?? before?.nightMode,
                                                   speakers: sound.speakers ?? before?.speakers))
        }
        send(to: device) { client in _ = try await client.setSound(device: device.id, sound) }
    }

    /// Shows `change` on the device's now playing straight away, and holds back reports that don't know of it yet.
    private func show(_ deviceId: String, _ change: (DeviceNowPlaying) -> DeviceNowPlaying) {
        guard let index = devices.firstIndex(where: { $0.id == deviceId }), let nowPlaying = devices[index].nowPlaying else { return }
        devices[index].nowPlaying = change(nowPlaying)
        holdUntil[deviceId] = Date().addingTimeInterval(Self.hold)
    }

    private func send(to device: Device, _ command: @escaping @MainActor (FlexarrClient) async throws -> Void) {
        guard let client else { return }
        Task {
            do {
                try await command(client)
            } catch FlexarrClientError.http(status: 409, let message) {
                tell(message ?? "Flexarr isn't open on \(device.name).")
            } catch {
                tell(ConnectionError(error).message)
            }
            // Once the TV has had a moment to report, what it says.
            try? await Task.sleep(for: .seconds(Self.hold))
            holdUntil[device.id] = nil
            await refresh()
        }
    }

    private func tell(_ message: String) {
        problem = message
        problemTask?.cancel()
        problemTask = Task {
            try? await Task.sleep(for: .seconds(4))
            if !Task.isCancelled { problem = nil }
        }
    }
}

private extension DeviceNowPlaying {
    /// The same, with these changed.
    func with(position: Double? = nil, paused: Bool? = nil, rate: Double?? = nil, prompt: PlayerPrompt?? = nil, tracks: PlayerTracks?? = nil,
              sound: DeviceSound?? = nil) -> DeviceNowPlaying {
        DeviceNowPlaying(item: item, positionSeconds: position ?? positionSeconds, durationSeconds: durationSeconds, paused: paused ?? self.paused,
                   reportedAt: position == nil && paused == nil ? reportedAt : Date(), rate: rate ?? self.rate, prompt: prompt ?? self.prompt,
                   hasNext: hasNext, tracks: tracks ?? self.tracks, sound: sound ?? self.sound)
    }

    /// Paused or playing from where it's got to now.
    func moved(paused: Bool) -> DeviceNowPlaying { with(position: position(at: Date()), paused: paused) }

    /// At `seconds` from now on.
    func moved(to seconds: Double) -> DeviceNowPlaying {
        with(position: max(0, durationSeconds > 0 ? min(seconds, durationSeconds) : seconds), paused: paused)
    }
}

private extension [PlayerTrack] {
    /// The same tracks with only `id` selected (none: nil).
    func selecting(_ id: String?) -> [PlayerTrack] {
        map { PlayerTrack(id: $0.id, label: $0.label, selected: $0.id == id) }
    }
}
