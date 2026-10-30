import ActivityKit
import FlexarrKit
import Foundation
import Observation

private let log = FlexarrLog("remote")

/// The TVs in the house playing something, for the Playing on TV bar and the remote: kept up to date by the command
/// stream's now-playing events (`AppModel.onNowPlaying`), with the device list asked for now and then in case the
/// stream isn't there (an API-key sign-in, an older server). Commands change what's shown at once, as asked, and the
/// TV's own reports put it right if it didn't happen.
@MainActor
@Observable
final class TVRemote {
    /// Every device signed in, as last heard.
    private var devices: [Device] = [] {
        didSet { syncActivity() }
    }
    /// The device the remote is on; nil: the one playing that was used last.
    var selectedId: String?
    /// The remote is open: the list is asked for more often.
    var isOpen = false
    /// Why the last command didn't work, to show for a moment.
    private(set) var problem: String?
    /// The app is in front: only then can a Live Activity start.
    @ObservationIgnored var isActive = false {
        didSet {
            guard isActive, !oldValue else { return }
            syncActivity()
            reregister()
        }
    }

    @ObservationIgnored private var client: FlexarrClient?
    /// After a command, reports already on their way still say how it was: they're held back for a moment, then the
    /// list is asked for again.
    @ObservationIgnored private var holdUntil: [String: Date] = [:]
    @ObservationIgnored private var problemTask: Task<Void, Never>?
    /// The device list has been heard since signing in, so an empty one means nothing's playing (not "not asked yet").
    @ObservationIgnored private var heard = false
    /// Live Activities by id: the work following each one's push token and end, and the token the server was given.
    @ObservationIgnored private var activityWork: [String: Task<Void, Never>] = [:]
    @ObservationIgnored private var activityTokens: [String: String] = [:]
    /// A Live Activity being started (its picture is saved first), by device and item.
    @ObservationIgnored private var starting: String?

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
        let previous = self.client
        self.client = client
        heard = false
        devices = []
        selectedId = nil
        if previous != nil {
            // Signed out, or another server: its Live Activities go.
            for activity in Activity<TVPlayingAttributes>.activities { end(activity) }
        } else if let client {
            // Opened again: the activities still showing carry on, their tokens given to the server again.
            for activity in Activity<TVPlayingAttributes>.activities where activity.activityState == .active || activity.activityState == .stale {
                follow(activity.id, deviceId: activity.attributes.deviceId, itemId: activity.attributes.itemId, client: client)
            }
        }
    }

    func refresh() async {
        guard let client, let list = try? await client.devices() else { return }
        heard = true
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

    // MARK: Live Activity

    /// The Live Activity follows the TV the remote is on: started (while the app is in front) when it plays, its times
    /// kept up to date while the app is open (the server takes over when it isn't), ended when it stops or moves on.
    private func syncActivity() {
        guard heard, ActivityAuthorizationInfo().areActivitiesEnabled else { return }
        let target = current
        let live = Activity<TVPlayingAttributes>.activities.filter { $0.activityState == .active || $0.activityState == .stale }
        for activity in live {
            if let device = target, let playing = device.nowPlaying, device.id == activity.attributes.deviceId, playing.item.id == activity.attributes.itemId {
                let state = TVPlayingAttributes.ContentState(playing)
                let id = activity.id
                if state.isNews(after: activity.content.state) { Task.detached { await Self.update(id, to: state) } }
            } else {
                end(activity)
            }
        }
        guard isActive, let device = target, let playing = device.nowPlaying,
              !live.contains(where: { $0.attributes.deviceId == device.id && $0.attributes.itemId == playing.item.id }) else { return }
        start(device, playing)
    }

    /// Gives the server the tokens of the Live Activities going again (back in front): a server that restarted has forgotten them.
    private func reregister() {
        guard let client else { return }
        for activity in Activity<TVPlayingAttributes>.activities where activity.activityState == .active || activity.activityState == .stale {
            guard let token = activityTokens[activity.id] else { continue }
            let deviceId = activity.attributes.deviceId
            let itemId = activity.attributes.itemId
            Task { try? await client.startLiveActivity(device: deviceId, token: token, environment: PushNotifications.environment, itemId: itemId) }
        }
    }

    private func start(_ device: Device, _ playing: DeviceNowPlaying) {
        let key = "\(device.id)#\(playing.item.id)"
        guard starting != key, let client else { return }
        starting = key
        Task {
            defer { if starting == key { starting = nil } }
            let artwork = await Self.savedArtwork(for: playing.item, client: client)
            // Moved on while the picture was saved.
            guard current?.id == device.id, current?.nowPlaying?.item.id == playing.item.id, isActive else { return }
            let item = playing.item
            let attributes = TVPlayingAttributes(deviceId: device.id, deviceName: device.name, itemId: item.id, title: item.displayTitle,
                                                 subtitle: item.episodeSubtitle, accent: item.accentColor, artworkFile: artwork)
            do {
                let activity = try Activity.request(attributes: attributes, content: ActivityContent(state: .init(playing), staleDate: nil), pushType: .token)
                log.info("Live Activity started for \(device.name)")
                follow(activity.id, deviceId: device.id, itemId: item.id, client: client)
            } catch {
                log.info("Couldn't start a Live Activity", error: error)
            }
        }
    }

    /// Gives the server each push token the activity gets, and tells it when the activity is gone (ended here, or
    /// swiped away).
    private func follow(_ id: String, deviceId: String, itemId: String, client: FlexarrClient) {
        let tokens: @Sendable (String) async -> Void = { [weak self] token in
            await MainActor.run { self?.activityTokens[id] = token }
            do {
                try await client.startLiveActivity(device: deviceId, token: token, environment: PushNotifications.environment, itemId: itemId)
            } catch {
                log.info("The server didn't take the Live Activity (a token of \(token.count) characters)", error: error)
            }
        }
        let gone: @Sendable () async -> Void = { [weak self] in
            if let token = await MainActor.run(body: { self?.activityTokens.removeValue(forKey: id) }) {
                try? await client.endLiveActivity(device: deviceId, token: token)
            }
        }
        activityWork[id] = Task.detached {
            await withTaskGroup(of: Void.self) { group in
                group.addTask { await Self.pushTokens(of: id, tokens) }
                group.addTask { await Self.waitForEnd(of: id); await gone() }
                await group.next()
                group.cancelAll()
            }
        }
    }

    private func end(_ activity: Activity<TVPlayingAttributes>) {
        let id = activity.id
        let deviceId = activity.attributes.deviceId
        if let token = activityTokens.removeValue(forKey: id), let client {
            Task { try? await client.endLiveActivity(device: deviceId, token: token) }
        }
        activityWork.removeValue(forKey: id)?.cancel()
        Task.detached { await Self.end(id) }
    }

    // Each looks the activity up by its id: an `Activity` can't be handed between tasks.

    private nonisolated static func activity(_ id: String) -> Activity<TVPlayingAttributes>? {
        Activity<TVPlayingAttributes>.activities.first { $0.id == id }
    }

    private nonisolated static func update(_ id: String, to state: TVPlayingAttributes.ContentState) async {
        await activity(id)?.update(ActivityContent(state: state, staleDate: nil))
    }

    private nonisolated static func end(_ id: String) async {
        await activity(id)?.end(nil, dismissalPolicy: .immediate)
    }

    private nonisolated static func pushTokens(of id: String, _ received: @Sendable (String) async -> Void) async {
        guard let activity = activity(id) else { return }
        for await data in activity.pushTokenUpdates {
            await received(data.map { String(format: "%02x", $0) }.joined())
        }
    }

    private nonisolated static func waitForEnd(of id: String) async {
        guard let activity = activity(id) else { return }
        for await state in activity.activityStateUpdates where state == .ended || state == .dismissed { return }
    }

    /// A small 16:9 picture for the Live Activity, in the widgets' folder; older ones go.
    private static func savedArtwork(for item: ItemSummary, client: FlexarrClient) async -> String? {
        let name = "tv-\(item.id).jpg"
        guard let url = PodcastSnapshot.artwork(name), let folder = PodcastSnapshot.folder else { return nil }
        if let files = try? FileManager.default.contentsOfDirectory(atPath: folder.path(percentEncoded: false)) {
            for file in files where file.hasPrefix("tv-") && file != name { try? FileManager.default.removeItem(at: folder.appending(path: file)) }
        }
        if FileManager.default.fileExists(atPath: url.path(percentEncoded: false)) { return name }
        let path = item.kind == .episode ? (item.artwork.thumb ?? item.artwork.backdrop) : (item.artwork.backdrop ?? item.artwork.thumb)
        guard let source = client.imageURL(path, width: 320), let image = await ImageLoader.shared.load(source),
              let data = image.jpegData(compressionQuality: 0.75) else { return nil }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? data.write(to: url, options: .atomic)
        return name
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

extension TVPlayingAttributes.ContentState {
    init(_ playing: DeviceNowPlaying) {
        self.init(paused: playing.paused, position: playing.positionSeconds, at: (playing.reportedAt ?? Date()).timeIntervalSince1970,
                  duration: playing.durationSeconds, rate: playing.rate ?? 1)
    }

    /// Shows something the activity's clock, running from `shown`, wouldn't: a pause, a new speed, a seek.
    func isNews(after shown: Self) -> Bool {
        if paused != shown.paused || abs(rate - shown.rate) > 0.01 || abs(duration - shown.duration) > 1 { return true }
        return abs(position(at: Date(timeIntervalSince1970: at)) - shown.position(at: Date(timeIntervalSince1970: at))) > 3
    }
}
