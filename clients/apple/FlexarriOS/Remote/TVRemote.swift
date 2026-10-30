import ActivityKit
import FlexarrKit
import Foundation
import Observation
import UIKit

private let log = FlexarrLog("remote")

/// The TVs in the house playing something, for the Playing on TV bar and the remote: kept up to date by the command
/// stream's now-playing events (`AppModel.onNowPlaying`), with the device list asked for now and then in case the
/// stream isn't there (an API-key sign-in, an older server). Commands change what's shown at once, as asked, and the
/// TV's own reports put it right if it didn't happen.
@MainActor
@Observable
final class TVRemote {
    /// Something to say for a moment over the bars or in the remote: a command that didn't work, or Stop with Undo.
    struct Notice: Equatable {
        let text: String
        /// Stopped on the TV: Undo plays it there again from where it stopped.
        var undo: (deviceId: String, itemId: String)? = nil

        static func == (a: Notice, b: Notice) -> Bool { a.text == b.text && a.undo?.deviceId == b.undo?.deviceId && a.undo?.itemId == b.undo?.itemId }
    }

    /// Every device signed in, as last heard.
    private var devices: [Device] = [] {
        didSet {
            playing = devices.playingTVs()
            syncActivity()
        }
    }
    /// The TVs playing something, the one used last first (worked out when the list changes, not on every look).
    private(set) var playing: [Device] = []
    /// The device the remote is on; nil: the one playing that was used last.
    var selectedId: String?
    /// The remote is showing.
    var showsRemote = false {
        didSet { if showsRemote != oldValue { wake() } }
    }
    private(set) var notice: Notice?
    /// Counts the viewer's own taps, and the commands that failed: what the haptics follow (not the TV's state, which
    /// someone else may change).
    private(set) var taps = 0
    private(set) var failures = 0
    /// The episode just asked for from the remote's list, until the TV starts it.
    private(set) var starting: String?
    /// The bar was put away for this TV and title; it comes back with the next title.
    private var hiddenBar: String?
    /// The app is in front: only then can a Live Activity start.
    @ObservationIgnored var isActive = false {
        didSet {
            guard isActive != oldValue else { return }
            // Back in front, what's known is old: Live Activities aren't ended on it until the list has been asked for again.
            fresh = false
            if isActive { reregister() }
        }
    }

    @ObservationIgnored private var client: FlexarrClient?
    /// After a command, reports already on their way still say how it was: they're held back until this time, then the
    /// list is asked for again if the stream hasn't said.
    @ObservationIgnored private var holdUntil: [String: Date] = [:]
    /// When the stream last said something about each device.
    @ObservationIgnored private var heardAt: [String: Date] = [:]
    /// Devices the stream named that the list didn't have (browsers, before servers left them out): asked about once.
    @ObservationIgnored private var unknown: Set<String> = []
    /// "Nothing playing" waits a moment before it's believed: a TV moving on to the next episode stops one playback
    /// before it starts the next.
    @ObservationIgnored private var goneLater: [String: Task<Void, Never>] = [:]
    @ObservationIgnored private var noticeTask: Task<Void, Never>?
    /// The device list has been heard since signing in, so an empty one means nothing's playing (not "not asked yet").
    @ObservationIgnored private var heard = false
    /// The list has been asked for since coming back to the front.
    @ObservationIgnored private var fresh = false
    /// Live Activities by id: the work following each one's push token and end, and the token the server was given.
    @ObservationIgnored private var activityWork: [String: Task<Void, Never>] = [:]
    @ObservationIgnored private var activityTokens: [String: String] = [:]
    /// A Live Activity being started (its picture is saved first), by device and item.
    @ObservationIgnored private var startingActivity: String?
    /// Live Activities swiped away, by device and item: not started again for that title.
    @ObservationIgnored private var dismissed: Set<String> = []

    private static let hold: TimeInterval = 1.5
    private static let goneGrace: Duration = .seconds(5)

    /// The device the remote is on.
    var current: Device? {
        playing.first { $0.id == selectedId } ?? playing.first
    }

    /// The TV the Playing on TV bar shows: the remote's, unless its bar was put away for what it's playing.
    var barDevice: Device? {
        guard let device = current, let item = device.nowPlaying?.item.id, Self.key(device.id, item) != hiddenBar else { return nil }
        return device
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
        if let previous {
            // Signed out, or another server: its Live Activities go, and that server hears so.
            for activity in Activity<TVPlayingAttributes>.activities { end(activity, telling: previous) }
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
        fresh = true
        let now = Date()
        // What a command just changed stays as asked until the hold is over.
        let merged = list.map { device in
            if let until = holdUntil[device.id], until > now, let shown = devices.first(where: { $0.id == device.id }) { return shown }
            return device
        }
        if merged != devices { devices = merged } else { syncActivity() }
        if let starting, playing.contains(where: { $0.nowPlaying?.item.id == starting }) { self.starting = nil }
    }

    /// The command stream says what a device is playing now, and whether Flexarr is open on it.
    func received(_ deviceId: String, _ nowPlaying: DeviceNowPlaying?, online: Bool?) {
        heardAt[deviceId] = Date()
        if let until = holdUntil[deviceId], until > Date() { return }
        guard let index = devices.firstIndex(where: { $0.id == deviceId }) else {
            // A device this phone hasn't heard of yet: the list once, not on every report.
            if unknown.insert(deviceId).inserted { Task { await refresh() } }
            return
        }
        if let online { devices[index].online = online }
        goneLater.removeValue(forKey: deviceId)?.cancel()
        if nowPlaying == nil, devices[index].nowPlaying != nil {
            goneLater[deviceId] = Task { [weak self] in
                try? await Task.sleep(for: Self.goneGrace)
                guard !Task.isCancelled, let self, let index = self.devices.firstIndex(where: { $0.id == deviceId }) else { return }
                self.devices[index].nowPlaying = nil
            }
            return
        }
        devices[index].nowPlaying = nowPlaying
        if let starting, nowPlaying?.item.id == starting { self.starting = nil }
    }

    /// Asks for the list while the app is up. The stream says as things change; this catches what it can't: often while
    /// the remote is open and the stream is quiet about a playing TV (it reports every ten seconds), now and then otherwise.
    func follow() async {
        var last = Date.distantPast
        while !Task.isCancelled {
            let quiet = current.map { Date().timeIntervalSince(heardAt[$0.id] ?? .distantPast) > 15 } ?? true
            let every: TimeInterval = showsRemote && quiet ? 5 : 30
            if Date().timeIntervalSince(last) >= every {
                last = Date()
                await refresh()
            }
            try? await Task.sleep(for: .seconds(1))
        }
    }

    /// The remote opened or closed: the next look comes now rather than at the next slow poll.
    private func wake() {
        guard showsRemote else { return }
        Task { await refresh() }
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

    /// Next, previous or skipping the intro: the TV says what came of it. Moving on holds back the TV's "nothing
    /// playing" in between.
    func control(_ action: DeviceControl) {
        guard let device = current, device.takes(action.kind) else { return }
        if action == .skipSegment { show(device.id) { $0.with(prompt: .some(nil)) } } else { holdUntil[device.id] = Date().addingTimeInterval(4) }
        send(to: device) { client in _ = try await client.control(device: device.id, action) }
    }

    /// Plays one of the season's episodes on the TV, marked as starting until the TV says so.
    func play(_ episode: ItemSummary) {
        guard let device = current else { return }
        starting = episode.id
        holdUntil[device.id] = Date().addingTimeInterval(4)
        send(to: device, failed: { [weak self] in self?.starting = nil }) { client in _ = try await client.play(on: device.id, itemId: episode.id) }
        // A TV that never starts it: the row stops saying so.
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(15))
            if self?.starting == episode.id { self?.starting = nil }
        }
    }

    /// Stops playing on the TV; true when the TV took it. The bar goes at once, and comes back if it didn't. With
    /// `offeringUndo`, says "Stopped" with Undo for a few seconds.
    @discardableResult
    func stop(offeringUndo: Bool = true) async -> Bool {
        guard let device = current, let client, let index = devices.firstIndex(where: { $0.id == device.id }) else { return false }
        taps += 1
        let item = device.nowPlaying?.item.id
        let before = devices[index].nowPlaying
        devices[index].nowPlaying = nil
        let until = Date().addingTimeInterval(Self.hold)
        holdUntil[device.id] = until
        do {
            let result = try await client.control(device: device.id, .stop)
            guard result.delivered else { throw FlexarrClientError.http(status: 409, message: "Flexarr isn't open on \(device.name).") }
            if offeringUndo, let item { say(Notice(text: "Stopped", undo: (device.id, item)), for: 6) }
            return true
        } catch {
            if let index = devices.firstIndex(where: { $0.id == device.id }), devices[index].nowPlaying == nil { devices[index].nowPlaying = before }
            fail(error, device: device)
            return false
        }
    }

    /// Undo for Stop: plays it on the TV again, from where it stopped.
    func undoStop() {
        guard let undo = notice?.undo, let client else { return }
        notice = nil
        taps += 1
        Task {
            do {
                _ = try await client.play(on: undo.deviceId, itemId: undo.itemId)
            } catch {
                fail(error, device: devices.first { $0.id == undo.deviceId })
            }
            try? await Task.sleep(for: .seconds(Self.hold))
            await refresh()
        }
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
        guard let device = current, device.takes(.sound) else { return }
        show(device.id) { playing in
            let before = playing.sound
            return playing.with(sound: DeviceSound(dialogueBoost: sound.dialogueBoost ?? before?.dialogueBoost, nightMode: sound.nightMode ?? before?.nightMode,
                                                   speakers: sound.speakers ?? before?.speakers))
        }
        send(to: device) { client in _ = try await client.setSound(device: device.id, sound) }
    }

    /// Puts the bar away until the TV plays something else (nothing on the TV changes).
    func hideBar() {
        guard let device = current, let item = device.nowPlaying?.item.id else { return }
        hiddenBar = Self.key(device.id, item)
    }

    /// Shows `change` on the device's now playing straight away, and holds back reports that don't know of it yet.
    private func show(_ deviceId: String, _ change: (DeviceNowPlaying) -> DeviceNowPlaying) {
        guard let index = devices.firstIndex(where: { $0.id == deviceId }), let nowPlaying = devices[index].nowPlaying else { return }
        devices[index].nowPlaying = change(nowPlaying)
        holdUntil[deviceId] = max(holdUntil[deviceId] ?? .distantPast, Date().addingTimeInterval(Self.hold))
    }

    private func send(to device: Device, failed: (@MainActor () -> Void)? = nil, _ command: @escaping @MainActor (FlexarrClient) async throws -> Void) {
        guard let client else { return }
        taps += 1
        let sentAt = Date()
        Task {
            do {
                try await command(client)
            } catch {
                failed?()
                fail(error, device: device)
            }
            // Once the TV has had a moment to report: the hold ends (unless a later command renewed it), and the list
            // is asked for only if the stream hasn't said anything since.
            try? await Task.sleep(for: .seconds(Self.hold))
            if let until = holdUntil[device.id], until <= Date() { holdUntil[device.id] = nil }
            if (heardAt[device.id] ?? .distantPast) < sentAt { await refresh() }
        }
    }

    private func fail(_ error: Error, device: Device?) {
        failures += 1
        if case FlexarrClientError.http(status: 409, let message) = error {
            say(Notice(text: message ?? "Flexarr isn't open on \(device?.name ?? "the TV")."))
        } else {
            say(Notice(text: ConnectionError(error).message))
        }
    }

    private func say(_ notice: Notice, for seconds: Double = 4) {
        self.notice = notice
        if notice.undo == nil { AccessibilityNotification.Announcement(notice.text).post() }
        noticeTask?.cancel()
        noticeTask = Task {
            try? await Task.sleep(for: .seconds(seconds))
            if !Task.isCancelled { self.notice = nil }
        }
    }

    private static func key(_ deviceId: String, _ itemId: String) -> String { "\(deviceId)#\(itemId)" }

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
                if RemoteTimes(state).isNews(after: RemoteTimes(activity.content.state)) { Task.detached { await Self.update(id, to: state) } }
            } else if fresh {
                // Only on a list asked for since coming back to the front: an old one would end what's still playing.
                end(activity)
            }
        }
        guard isActive, let device = target, let playing = device.nowPlaying, !dismissed.contains(Self.key(device.id, playing.item.id)),
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
        let key = Self.key(device.id, playing.item.id)
        guard startingActivity != key, let client else { return }
        startingActivity = key
        Task {
            defer { if startingActivity == key { startingActivity = nil } }
            let artwork = await Self.savedArtwork(for: playing.item, client: client)
            // Moved on while the picture was saved.
            guard current?.id == device.id, current?.nowPlaying?.item.id == playing.item.id, isActive else { return }
            let item = playing.item
            let attributes = TVPlayingAttributes(deviceId: device.id, deviceName: device.name, itemId: item.id, title: item.displayTitle,
                                                 subtitle: item.episodeSubtitle, accent: item.accentColor, artworkFile: artwork,
                                                 canSkip: device.takes(.skip), isAppleTV: device.deviceType != .androidTv)
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
    /// swiped away, which is remembered so it isn't started again for this title).
    private func follow(_ id: String, deviceId: String, itemId: String, client: FlexarrClient) {
        let tokens: @Sendable (String) async -> Void = { [weak self] token in
            await MainActor.run { self?.activityTokens[id] = token }
            do {
                try await client.startLiveActivity(device: deviceId, token: token, environment: PushNotifications.environment, itemId: itemId)
            } catch {
                log.info("The server didn't take the Live Activity", error: error)
            }
        }
        let gone: @Sendable (Bool) async -> Void = { [weak self] swiped in
            let token = await MainActor.run { () -> String? in
                if swiped { self?.dismissed.insert(Self.key(deviceId, itemId)) }
                return self?.activityTokens.removeValue(forKey: id)
            }
            if let token { try? await client.endLiveActivity(device: deviceId, token: token) }
        }
        activityWork[id] = Task.detached {
            let swiped = await withTaskGroup(of: Bool?.self) { group -> Bool in
                group.addTask { await Self.pushTokens(of: id, tokens); return nil }
                group.addTask { await Self.waitForEnd(of: id) }
                var swiped = false
                if let first = await group.next(), let ended = first { swiped = ended }
                group.cancelAll()
                return swiped
            }
            // Outside the group, so it isn't cancelled with it; and whichever finished first.
            guard !Task.isCancelled else { return }
            await gone(swiped)
        }
    }

    private func end(_ activity: Activity<TVPlayingAttributes>, telling server: FlexarrClient? = nil) {
        let id = activity.id
        let deviceId = activity.attributes.deviceId
        if let token = activityTokens.removeValue(forKey: id), let client = server ?? client {
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

    /// Waits for the activity to go: true when it was swiped away, false when it ended.
    private nonisolated static func waitForEnd(of id: String) async -> Bool? {
        guard let activity = activity(id) else { return false }
        for await state in activity.activityStateUpdates {
            if state == .dismissed { return true }
            if state == .ended { return false }
        }
        return nil
    }

    /// The Lock Screen's buttons (`TVRemoteIntent`): shown on the activity at once, then sent to the TV. Throws when the
    /// TV didn't take it, so the system shows the button failed; the activity goes back as it was.
    nonisolated static func lockScreen(deviceId: String, action: String, client: FlexarrClient) async throws {
        let activity = Activity<TVPlayingAttributes>.activities.first { $0.attributes.deviceId == deviceId && $0.activityState == .active }
        let before = activity?.content.state
        if var state = before {
            let now = Date().timeIntervalSince1970
            state.position = state.position(at: Date())
            state.at = now
            switch action {
            case "pause": state.paused = true
            case "resume": state.paused = false
            case "back": state.position = max(0, state.position - 10)
            case "forward": state.position = state.duration > 0 ? min(state.duration, state.position + 10) : state.position + 10
            default: break
            }
            await activity?.update(ActivityContent(state: state, staleDate: nil))
        }
        do {
            switch action {
            case "pause", "resume":
                let result = try await client.control(device: deviceId, action == "pause" ? .pause : .resume)
                if !result.delivered { throw FlexarrClientError.http(status: 409, message: nil) }
            case "back": try await client.skip(device: deviceId, by: -10)
            case "forward": try await client.skip(device: deviceId, by: 10)
            default: break
            }
        } catch {
            if let before { await activity?.update(ActivityContent(state: before, staleDate: nil)) }
            throw error
        }
    }

    /// A small 16:9 picture for the Live Activity, in the widgets' folder; older ones go. Off the main thread.
    private nonisolated static func savedArtwork(for item: ItemSummary, client: FlexarrClient) async -> String? {
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
        let times = RemoteTimes(playing)
        self.init(paused: times.paused, position: times.position, at: times.at, duration: times.duration, rate: times.rate)
    }
}

extension RemoteTimes {
    init(_ state: TVPlayingAttributes.ContentState) {
        self.init(paused: state.paused, position: state.position, at: state.at, duration: state.duration, rate: state.rate)
    }
}
