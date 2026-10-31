import Foundation

public enum FlexarrClientError: Error, Equatable, LocalizedError {
    /// The server rejected the API key (or one is required and none was sent).
    case unauthorized
    case http(status: Int, message: String?)
    /// The response was not what a Flexarr server sends (for example an HTML page).
    case unexpectedResponse
    case invalidResponse
    case invalidAddress
    /// Neither the home address nor the one for away from home could be reached.
    case unreachable
    /// A channel couldn't be watched: every stream is recording. Says what, so the player can offer it instead.
    case recordingInTheWay(RecordingInTheWay)
    /// This takes the admin PIN (leaving a kids profile, locking or releasing a device), which wasn't
    /// given or wasn't right. The message is the server's.
    case parentPinNeeded(message: String?)
    /// The admin PIN was right but the chosen profile's own PIN wasn't. The message is the server's.
    case ownPinWrong(message: String?)
    /// This device is locked to kids profiles, so only they can be chosen.
    case kidsLocked(message: String?)

    public var errorDescription: String? {
        switch self {
        case .unauthorized:
            "The server didn’t accept this device’s sign-in. Connect it again with a code, or if you typed an API key, check it against Settings › Access on the web. Over Tailscale, your Tailscale account needs linking in Settings › Access."
        case .recordingInTheWay(let recording):
            recording.message
        case .parentPinNeeded(let message):
            message ?? "Enter the admin PIN."
        case .ownPinWrong(let message):
            message ?? "That PIN isn’t right."
        case .kidsLocked(let message):
            message ?? "Child lock is on for this device."
        case .http(let status, let message):
            message ?? "The server answered with an error (HTTP \(status))."
        case .unexpectedResponse:
            "That address answered, but not like a Flexarr server. Check the host name and port."
        case .invalidResponse:
            "The server sent a response that couldn't be read."
        case .invalidAddress:
            "That doesn't look like a server address. Try something like http://192.168.1.20:8383."
        case .unreachable:
            "Couldn't reach the server, at home or over Tailscale. Make sure Flexarr is running, and that Tailscale is connected on this device."
        }
    }
}

/// Talks to a Flexarr server's `/api/v1` REST API. Shared by the tvOS, iOS and macOS apps.
public struct FlexarrClient: Sendable {
    /// What this device is ("AppleTv", "IPhone" or "IPad"), sent with every request as `X-Flexarr-Device` so the
    /// server's lists of devices can name and draw it. Set once at launch; nil sends nothing.
    public nonisolated(unsafe) static var deviceType: String?

    /// The server's addresses; requests go to whichever answered last (see `ServerAddresses`).
    public let addresses: ServerAddresses
    /// The address in use now: streams and artwork are resolved against it.
    public var baseURL: URL { addresses.current }
    public let apiKey: String?
    /// The chosen profile's token (from `selectProfile`); nil acts as the main profile.
    public let profileToken: String?
    private let session: URLSession
    /// Called whenever the server answers 401, so an app can notice its saved sign-in has stopped working.
    private let onUnauthorized: (@Sendable () -> Void)?

    public init(baseURL: URL, apiKey: String? = nil, profileToken: String? = nil, session: URLSession = .shared,
                onUnauthorized: (@Sendable () -> Void)? = nil) {
        self.init(addresses: ServerAddresses(home: baseURL, remote: nil), apiKey: apiKey, profileToken: profileToken,
                  session: session, onUnauthorized: onUnauthorized)
    }

    /// A client that can reach the server at home or away (see `ServerAddresses`).
    public init(addresses: ServerAddresses, apiKey: String? = nil, profileToken: String? = nil, session: URLSession = .shared,
                onUnauthorized: (@Sendable () -> Void)? = nil) {
        self.addresses = addresses
        self.apiKey = apiKey
        self.profileToken = profileToken
        self.session = session
        self.onUnauthorized = onUnauthorized
    }

    /// Turns what a person typed ("mini.local", "192.168.1.5:8383", "http://host:8383/")
    /// into a base URL. Defaults to http and Flexarr's port 8383 when they're left out.
    public static func normalizeAddress(_ input: String) -> URL? {
        var text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, !text.contains(" ") else { return nil }
        if !text.contains("://") { text = "http://" + text }
        guard var components = URLComponents(string: text),
              let scheme = components.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = components.host, !host.isEmpty else { return nil }
        components.scheme = scheme
        if components.port == nil && !input.contains("://") { components.port = 8383 }
        while components.path.hasSuffix("/") { components.path.removeLast() }
        components.query = nil
        components.fragment = nil
        return components.url
    }

    // MARK: Endpoints

    public func systemStatus() async throws -> SystemStatus {
        try await get("system/status")
    }

    public func libraries() async throws -> [Library] {
        try await get("libraries")
    }

    public func libraryItems(_ libraryId: String, sort: LibrarySort = .title, genre: String? = nil,
                             skip: Int = 0, take: Int = 60) async throws -> Paged<ItemSummary> {
        var query: [URLQueryItem] = []
        if sort != .title { query.append(.init(name: "sort", value: sort.rawValue)) }
        if let genre, !genre.isEmpty { query.append(.init(name: "genre", value: genre)) }
        if skip > 0 { query.append(.init(name: "skip", value: String(skip))) }
        query.append(.init(name: "take", value: String(take)))
        return try await get("libraries/\(libraryId)/items", query: query)
    }

    public func libraryGenres(_ libraryId: String) async throws -> [GenreCount] {
        try await get("libraries/\(libraryId)/genres")
    }

    public func home() async throws -> Home {
        try await get("home")
    }

    /// A library's own home page: a hero and rows of just its titles (no hero for music).
    public func libraryBrowse(_ libraryId: String) async throws -> Home {
        try await get("libraries/\(libraryId)/browse")
    }

    public func item(_ id: String) async throws -> ItemDetail {
        try await get("items/\(id)")
    }

    public func similar(_ id: String) async throws -> [ItemSummary] {
        try await get("items/\(id)/similar")
    }

    /// Every track an artist (or one album) has, in the order a record collection plays: albums oldest first, then
    /// disc and track. Servers before the endpoint answer 404, so the albums are fetched one by one instead.
    public func tracks(of id: String) async throws -> [ItemSummary] {
        do {
            return try await get("items/\(id)/tracks")
        } catch FlexarrClientError.http(status: 404, _) {
            let detail = try await item(id)
            if detail.item.kind == .album { return detail.children.filter { $0.kind == .track } }
            var tracks: [ItemSummary] = []
            // Oldest album first, as the endpoint orders them; the server lists an artist's albums newest first.
            for album in detail.children.filter({ $0.kind == .album }).sorted(by: { ($0.year ?? .max, $0.title) < ($1.year ?? .max, $1.title) }) {
                tracks += try await item(album.id).children.filter { $0.kind == .track }
            }
            return tracks
        }
    }

    /// A track's lyrics, or nil when it has none (servers before lyrics have none for any track).
    public func lyrics(of id: String) async throws -> Lyrics? {
        do {
            return try await get("items/\(id)/lyrics")
        } catch FlexarrClientError.http(status: 404, _) {
            return nil
        }
    }

    /// The signed-in profile's music playlists. Servers before playlists have none.
    public func playlists() async throws -> [PlaylistSummary] {
        do {
            return try await get("playlists")
        } catch FlexarrClientError.http(status: 404, _) {
            return []
        }
    }

    /// A playlist with its tracks in order.
    public func playlist(_ id: String) async throws -> PlaylistDetail {
        try await get("playlists/\(id)")
    }

    /// The episode to play next for a series or season, or `nil` when everything is watched.
    public func nextUp(_ id: String) async throws -> ItemSummary? {
        let data = try await send("GET", "items/\(id)/next-up")
        return data.isEmpty ? nil : try decode(ItemSummary.self, from: data)
    }

    public func setWatched(_ id: String, watched: Bool) async throws {
        _ = try await send("POST", "items/\(id)/watched", body: WatchedRequest(watched: watched))
    }

    /// Takes a title out of Continue Watching without marking it watched; for an episode, its show stays out until
    /// another episode is played.
    public func removeFromContinueWatching(_ id: String) async throws {
        _ = try await send("DELETE", "home/continue/\(id)")
    }

    public func person(_ id: String) async throws -> Person {
        try await get("people/\(id)")
    }

    public func search(_ query: String) async throws -> SearchResults {
        try await get("search", query: [.init(name: "q", value: query)])
    }

    /// The profile's recent searches and the titles opened from them. Servers before 0.56 have none.
    public func recentSearches() async throws -> RecentSearches {
        do {
            return try await get("search/recent")
        } catch FlexarrClientError.http(status: 404, _) {
            return RecentSearches()
        }
    }

    /// A search result was picked: the words searched for, and the title when it was one (not a person or genre).
    /// Reopening a title from the recent row sends just the title, which moves it to the front.
    public func recordSearch(query: String?, itemId: String?) async throws {
        _ = try await send("POST", "search/recent", body: RecentSearchBody(query: query, itemId: itemId))
    }

    public func forgetRecentSearch(query: String) async throws {
        _ = try await send("DELETE", "search/recent", query: [.init(name: "query", value: query)])
    }

    public func forgetRecentItem(itemId: String) async throws {
        _ = try await send("DELETE", "search/recent", query: [.init(name: "itemId", value: itemId)])
    }

    public func clearRecentSearches() async throws {
        _ = try await send("DELETE", "search/recent")
    }

    private struct RecentSearchBody: Encodable {
        let query: String?
        let itemId: String?
    }

    public func startPlayback(_ request: PlaybackRequest) async throws -> PlaybackInfo {
        let data = try await send("POST", "playback", body: request)
        return try decode(PlaybackInfo.self, from: data)
    }

    /// `remote`: what a remote on another device needs to follow along (the Apple TV sends it); servers before 0.56 ignore it.
    public func reportProgress(sessionId: String, positionSeconds: Double, paused: Bool, ended: Bool = false, remote: RemoteReport? = nil) async throws {
        let report = ProgressReport(positionSeconds: max(0, positionSeconds), paused: paused, ended: ended, rate: remote?.rate, prompt: remote?.prompt,
                                    hasNext: remote?.hasNext, tracks: remote?.tracks, sound: remote?.sound)
        _ = try await send("POST", "playback/\(sessionId)/progress", body: report)
    }

    public func stopPlayback(sessionId: String) async throws {
        _ = try await send("DELETE", "playback/\(sessionId)")
    }

    /// The viewer picked subtitles in the player (a track's key, or nil for off): remembered for next time.
    public func setSubtitle(sessionId: String, key: String?) async throws {
        _ = try await send("PUT", "playback/\(sessionId)/subtitle", body: SubtitleChoice(key: key ?? "off"))
    }

    private struct SubtitleChoice: Encodable {
        let key: String
    }

    /// This session's scrubbing previews: nil until the server has made them (ask again later).
    public func trickplay(sessionId: String) async throws -> TrickplayManifest? {
        do {
            return try await get("playback/\(sessionId)/trickplay.json")
        } catch FlexarrClientError.http(status: 404, _) {
            return nil
        }
    }

    /// One sheet of a session's scrubbing previews. Needs no API key.
    public func trickplaySheetURL(sessionId: String, sheet: Int) -> URL? {
        url(for: "/api/v1/playback/\(sessionId)/trickplay/\(sheet).jpg")
    }

    // MARK: Downloads

    /// The sizes this title can be downloaded at, with about how big each would be.
    public func downloadOptions(_ itemId: String) async throws -> [DownloadOption] {
        try await get("items/\(itemId)/download-options")
    }

    /// Asks the server to get a title ready for downloading (or finds the one already asked for).
    public func requestDownload(_ itemId: String, quality: DownloadQuality) async throws -> OfflineDownload {
        let data = try await send("POST", "offline", body: DownloadRequestBody(itemId: itemId, quality: quality))
        return try decode(OfflineDownload.self, from: data)
    }

    public func download(_ id: String) async throws -> OfflineDownload {
        try await get("offline/\(id)")
    }

    /// The device has the file: the server can delete its copy.
    public func markDownloadReceived(_ id: String) async throws {
        _ = try await send("POST", "offline/\(id)/received")
    }

    public func deleteDownload(_ id: String) async throws {
        _ = try await send("DELETE", "offline/\(id)")
    }

    /// Watching done with no connection, recorded unless something newer was since.
    public func reportOfflineProgress(itemId: String, positionSeconds: Double, finished: Bool, playedAt: Date) async throws {
        let when = playedAt.formatted(.iso8601)
        _ = try await send("POST", "items/\(itemId)/offline-progress",
                           body: OfflineProgressBody(positionSeconds: max(0, positionSeconds), finished: finished, playedAt: when))
    }

    /// The request for a download's file, signed in, for a background download session (which can't use this client).
    public func downloadFileRequest(_ id: String) -> URLRequest {
        var request = URLRequest(url: baseURL.appending(path: "api/v1/offline/\(id)/file"))
        if let apiKey, !apiKey.isEmpty { request.setValue(apiKey, forHTTPHeaderField: "X-Api-Key") }
        if let profileToken, !profileToken.isEmpty { request.setValue(profileToken, forHTTPHeaderField: "X-Flexarr-Profile") }
        if let deviceType = Self.deviceType { request.setValue(deviceType, forHTTPHeaderField: "X-Flexarr-Device") }
        return request
    }

    /// The signed-in profile's auto-play and skip preferences.
    public func playbackPreferences() async throws -> PlaybackPreferences {
        try await get("me/playback-preferences")
    }

    public func setPlaybackPreferences(_ preferences: PlaybackPreferences) async throws {
        _ = try await send("PUT", "me/playback-preferences", body: preferences)
    }

    /// Remembers how far this title's subtitles are moved (later when positive); used the next time they're fetched.
    public func setSubtitleDelay(itemId: String, seconds: Double) async throws {
        _ = try await send("PUT", "items/\(itemId)/subtitle-delay", body: SubtitleDelayBody(seconds: seconds))
    }

    /// Plays a file's sound `seconds` later (earlier when negative, up to two seconds either way), for everyone in the
    /// household. Servers before 0.53 answer 404.
    public func setAudioDelay(itemId: String, seconds: Double) async throws {
        _ = try await send("PUT", "items/\(itemId)/audio-delay", body: AudioDelayBody(seconds: seconds))
    }

    /// Remembers `speed` for the show an episode belongs to, for this profile (1 forgets it). A film's isn't kept.
    /// Servers before 0.53 answer 404.
    public func setPlaybackSpeed(itemId: String, speed: Double) async throws {
        _ = try await send("PUT", "items/\(itemId)/playback-speed", body: PlaybackSpeedBody(speed: speed))
    }

    // MARK: Profiles

    public func profiles() async throws -> [Profile] {
        try await get("profiles")
    }

    /// Saves the order of a profile's libraries in every app's menu; `libraries()` lists them in it after. Servers
    /// before 0.54 answer 404.
    public func setLibraryOrder(profileId: String, order: [String]) async throws -> Profile {
        try decode(Profile.self, from: try await send("PUT", "profiles/\(profileId)/library-order", body: LibraryOrderBody(order: order)))
    }

    private struct LibraryOrderBody: Encodable {
        let order: [String]
    }

    /// Checks the PIN (if the profile has one) and returns the token for `profileToken`.
    /// A wrong PIN is a 403 whose message says so.
    public func selectProfile(_ id: String, pin: String? = nil, parentPin: String? = nil) async throws -> ProfileSelection {
        let data = try await send("POST", "profiles/\(id)/select", body: SelectProfileRequest(pin: pin, parentPin: parentPin))
        return try decode(ProfileSelection.self, from: data)
    }

    /// Locks this device to kids profiles, choosing `profileId` (else the one chosen here, or the first). Takes the
    /// admin PIN unless it's unlocked here; `parentPinNeeded` otherwise. Servers before 0.56 answer 404.
    public func lockToKids(pin: String?, profileId: String? = nil) async throws -> ProfileSelection {
        let data = try await send("POST", "profiles/kids-lock", body: KidsLockRequest(pin: pin, profileId: profileId))
        return try decode(ProfileSelection.self, from: data)
    }

    /// Releases this device from kids profiles, with the admin PIN (`parentPinNeeded` if it's wrong).
    public func releaseKidsLock(pin: String) async throws {
        _ = try await send("POST", "profiles/kids-lock/release", body: KidsLockRequest(pin: pin))
    }

    // MARK: Pairing (connecting without typing the API key)

    /// Asks the server for a code to show; someone signed in as the main profile allows it on their phone or in the web app.
    /// Says this app shows "Not allowed" itself after "Don't allow" (and asks again only on Try again), so the server
    /// asks the main profile about every code it asks for (servers before 0.56 ignore it).
    public func startPairing(deviceName: String) async throws -> PairingStart {
        let data = try await send("POST", "pairing", body: PairingStartBody(deviceName: deviceName, stopsWhenDeclined: true))
        return try decode(PairingStart.self, from: data)
    }

    /// Polled while the code is on screen; carries this Apple TV's own token once approved.
    public func pairingStatus(_ id: String) async throws -> PairingStatus {
        try await get("pairing/\(id)")
    }

    /// Devices showing a code right now, for the main profile's phone to allow or not (the command stream says when
    /// one starts; this catches up on any missed while the app was closed). Empty from servers before 0.56, which have
    /// no list; anyone but the main profile is refused (403).
    public func waitingPairings() async throws -> [PairingRequest] {
        try await waitingPairingsIfKept() ?? []
    }

    /// As `waitingPairings`, but nil from servers before 0.56, so a list of them can say there's none to show.
    public func waitingPairingsIfKept() async throws -> [PairingRequest]? {
        do {
            return try await get("pairing/waiting")
        } catch FlexarrClientError.http(status: 404, _) {
            return nil
        }
    }

    /// Lets the device showing `code` in. 404: it's no longer waiting (it may have expired and shows a new code);
    /// 429: too many allowed this hour, with the server's message.
    public func approvePairing(code: String) async throws {
        _ = try await send("POST", "pairing/code/\(code)/approve", body: [String: String]())
    }

    /// Turns the device showing `code` away; it stops waiting.
    public func declinePairing(code: String) async throws {
        _ = try await send("POST", "pairing/code/\(code)/decline", body: [String: String]())
    }

    /// Whether the server is new and nobody has set it up in a browser yet (so there's nobody to approve a code).
    /// False on servers from before this, which don't say.
    public func setupOpen() async -> Bool {
        await setupStatus()?.open ?? false
    }

    /// Whether the server is new, and whether setting it up asks for the code in its log. Nil when it doesn't say.
    public func setupStatus() async -> SetupStatus? {
        try? await get("setup")
    }

    // MARK: Devices (playing on another screen)

    /// Every Apple TV, iPhone and iPad signed in to the server, the ones with Flexarr open first.
    public func devices() async throws -> [Device] {
        try await get("devices")
    }

    /// Tells a device to play a title. A show's or season's id plays the episode that device's profile (or `profileId`,
    /// which the device switches to first) is up to; the result says which, and whether the device took it (Flexarr
    /// must be open on it). Servers before 0.54 ignore `profileId`; `startSeconds` (where to start a film, episode or
    /// track) servers and apps before 0.56.
    public func play(on deviceId: String, itemId: String, fromBeginning: Bool = false, shuffle: Bool = false, profileId: String? = nil,
                     startSeconds: Double? = nil) async throws -> DeviceCommandResult {
        let body = DevicePlayBody(itemId: itemId, fromBeginning: fromBeginning, shuffle: shuffle, profileId: profileId, startSeconds: startSeconds)
        let data = try await send("POST", "devices/\(deviceId)/play", body: body)
        return try decode(DeviceCommandResult.self, from: data)
    }

    /// Puts a Live TV channel on another device.
    public func watch(on deviceId: String, channelId: String) async throws -> DeviceCommandResult {
        let data = try await send("POST", "devices/\(deviceId)/watch", body: DeviceWatchBody(channelId: channelId))
        return try decode(DeviceCommandResult.self, from: data)
    }

    /// Pauses, resumes or stops what a device is playing.
    /// `handOff` with `.stop`: it carries on on this device (Continue on iPhone), which the TV says.
    public func control(device deviceId: String, _ action: DeviceControl, handOff: Bool = false) async throws -> DeviceCommandResult {
        let data = try await send("POST", "devices/\(deviceId)/\(action.rawValue)", query: handOff ? [.init(name: "handOff", value: "true")] : [])
        return try decode(DeviceCommandResult.self, from: data)
    }

    /// Moves what a device is playing to `seconds` in. Like the rest of a remote's commands (servers from 0.56): only to
    /// a device with Flexarr open whose app takes it (`Device.takes`), else a 409 with the reason.
    public func seek(device deviceId: String, to seconds: Double) async throws {
        _ = try await send("POST", "devices/\(deviceId)/seek", body: DeviceSeekBody(positionSeconds: max(0, seconds)))
    }

    /// Jumps what a device is playing by `seconds` (negative: back).
    public func skip(device deviceId: String, by seconds: Double) async throws {
        _ = try await send("POST", "devices/\(deviceId)/skip", body: DeviceSkipBody(seconds: seconds))
    }

    /// Changes a device's soundtrack to the player's track `trackId`.
    public func setAudioTrack(device deviceId: String, _ trackId: String) async throws {
        _ = try await send("POST", "devices/\(deviceId)/audio", body: DeviceTrackBody(trackId: trackId))
    }

    /// Shows the subtitles `trackId` on a device, or turns them off (nil).
    public func setSubtitleTrack(device deviceId: String, _ trackId: String?) async throws {
        _ = try await send("POST", "devices/\(deviceId)/subtitles", body: DeviceTrackBody(trackId: trackId))
    }

    /// Plays what's on a device at `rate` times normal speed (0.5 to 2).
    public func setRate(device deviceId: String, _ rate: Double) async throws {
        _ = try await send("POST", "devices/\(deviceId)/rate", body: DeviceRateBody(rate: rate))
    }

    /// Tells the server about this phone's Live Activity for what `deviceId` is playing (`itemId`), so it keeps the
    /// times up to date through the push relay while the phone is locked. Servers before 0.56 don't have it (a 404);
    /// one with notifications turned off says 409.
    public func startLiveActivity(device deviceId: String, token: String, environment: String, itemId: String) async throws {
        _ = try await send("PUT", "devices/\(deviceId)/activity", body: LiveActivityBody(token: token, environment: environment, itemId: itemId))
    }

    /// The Live Activity has ended: the server stops updating it.
    public func endLiveActivity(device deviceId: String, token: String) async throws {
        _ = try await send("DELETE", "devices/\(deviceId)/activity/\(token)")
    }

    /// Changes another device's sound settings. Reaches it only while Flexarr is open there: otherwise a 409. Servers
    /// before 0.56 don't have it (a 404).
    public func setSound(device deviceId: String, _ sound: DeviceSound) async throws -> DeviceCommandResult {
        let data = try await send("POST", "devices/\(deviceId)/sound", body: sound)
        return try decode(DeviceCommandResult.self, from: data)
    }

    /// Commands sent to this device, as they arrive (see `deviceEvents`, which this is the commands of).
    public func commandStream() -> AsyncThrowingStream<DeviceCommand, Error> {
        let events = deviceEvents()
        return AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    for try await event in events {
                        if case .command(let command) = event { continuation.yield(command) }
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// What the server tells this device, as it happens, over one long-lived request (server-sent events): commands
    /// for it and, signed in as the main profile, devices asking to connect. Ends when the server closes it (a newer
    /// connection from this device took over) or the connection drops; the caller opens it again. Only a device
    /// signed in with its own token has one: with the API key the server answers 403. Events it doesn't know (from
    /// newer servers) are skipped.
    ///
    /// `accepts`: the commands beyond the basic ones this device takes, for remotes to offer. `nowPlaying`: hear what
    /// every device is playing as it changes too (`.nowPlaying`), for a remote. Servers before 0.56 ignore both.
    public func deviceEvents(accepts: [DeviceCommandKind] = [], nowPlaying: Bool = false) -> AsyncThrowingStream<DeviceEvent, Error> {
        var url = baseURL.appending(path: "api/v1/devices/commands")
        var query: [URLQueryItem] = []
        if !accepts.isEmpty { query.append(URLQueryItem(name: "accepts", value: accepts.map(\.rawValue).joined(separator: ","))) }
        if nowPlaying { query.append(URLQueryItem(name: "nowPlaying", value: "true")) }
        if !query.isEmpty { url.append(queryItems: query) }
        var request = URLRequest(url: url)
        request.timeoutInterval = 90 // The server writes a comment every 20 seconds.
        request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        if let apiKey, !apiKey.isEmpty { request.setValue(apiKey, forHTTPHeaderField: "X-Api-Key") }
        if let profileToken, !profileToken.isEmpty { request.setValue(profileToken, forHTTPHeaderField: "X-Flexarr-Profile") }
        if let deviceType = Self.deviceType { request.setValue(deviceType, forHTTPHeaderField: "X-Flexarr-Device") }
        let prepared = request
        let session = self.session
        let onUnauthorized = self.onUnauthorized
        return AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    let (bytes, response) = try await session.bytes(for: prepared)
                    guard let http = response as? HTTPURLResponse else { throw FlexarrClientError.invalidResponse }
                    switch http.statusCode {
                    case 200..<300: break
                    case 401:
                        onUnauthorized?()
                        throw FlexarrClientError.unauthorized
                    default: throw FlexarrClientError.http(status: http.statusCode, message: nil)
                    }
                    // Split lines by hand: an event ends with an empty line, which `AsyncBytes.lines` leaves out.
                    var events = ServerSentEvents()
                    var line: [UInt8] = []
                    for try await byte in bytes {
                        guard byte == UInt8(ascii: "\n") else {
                            line.append(byte)
                            continue
                        }
                        if line.last == UInt8(ascii: "\r") { line.removeLast() }
                        let text = String(decoding: line, as: UTF8.self)
                        line.removeAll(keepingCapacity: true)
                        guard let event = events.feed(text) else { continue }
                        let data = Data(event.data.utf8)
                        switch event.name {
                        case "command":
                            continuation.yield(.command(try Self.decoder.decode(DeviceCommand.self, from: data)))
                        // A prompt that can't be read is left out rather than ending the stream (and the commands).
                        case "pairing":
                            if let request = try? Self.decoder.decode(PairingRequest.self, from: data) { continuation.yield(.pairing(request)) }
                        case "pairing-ended":
                            if let ended = try? Self.decoder.decode(PairingEnded.self, from: data) { continuation.yield(.pairingEnded(code: ended.code)) }
                        case "now-playing":
                            if let change = try? Self.decoder.decode(DeviceNowPlayingEvent.self, from: data) {
                                continuation.yield(.nowPlaying(deviceId: change.deviceId, change.nowPlaying, online: change.online))
                            }
                        default:
                            continue
                        }
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    // MARK: Sign-in

    /// How this client signed in, which profile it acts for, and whether it may manage the server.
    public func me() async throws -> SignInInfo {
        try await get("me")
    }

    /// Unlocks managing the server for this sign-in with the admin PIN, for a while (servers from 0.56, where
    /// choosing the main profile isn't enough by itself). 403: not the right PIN; 429: too many wrong ones, with the
    /// server's message; 409: the main profile has no PIN yet. Returns when it locks again.
    @discardableResult
    public func unlockAdmin(pin: String) async throws -> Date? {
        let data = try await send("POST", "admin/unlock", body: ["pin": pin])
        return try decode(AdminUnlock.self, from: data).unlockedUntil
    }

    /// Swaps the server's API key (what Apple TVs were given before per-device sign-ins) for a token of this
    /// device's own, which can be signed out from the web app without changing the key everywhere.
    public func deviceToken(deviceName: String) async throws -> String {
        let data = try await send("POST", "session/device", body: ["deviceName": deviceName])
        return try decode(DeviceToken.self, from: data).token
    }

    /// Signs this device out on the server (its token stops working).
    public func signOut() async throws {
        _ = try await send("DELETE", "session")
    }

    // MARK: Logs

    /// Warnings and errors from this app (at most `ClientLogQueue.batchSize`). The server answers 429 when too much
    /// is being sent. Who sent them, the server knows from the sign-in.
    public func sendLogs(platform: String, appVersion: String, entries: [ClientLogEntry]) async throws {
        _ = try await send("POST", "logs/client", body: ClientLogBatch(platform: platform, appVersion: appVersion, entries: entries))
    }

    /// The app's whole log, sent on purpose from Settings, for looking into a problem; returns its id on the server.
    public func sendDiagnostics(platform: String, appVersion: String, note: String?, text: String) async throws -> String {
        var query = [URLQueryItem(name: "platform", value: platform), URLQueryItem(name: "appVersion", value: appVersion)]
        if let note, !note.isEmpty { query.append(.init(name: "note", value: note)) }
        let data = try await send("POST", "logs/client/diagnostics", query: query, bodyData: Data(text.utf8),
                                  contentType: "text/plain; charset=utf-8", timeout: 120)
        return try decode(DiagnosticsReceipt.self, from: data).id
    }

    private struct ClientLogBatch: Encodable {
        let platform: String
        let appVersion: String
        let entries: [ClientLogEntry]
    }

    private struct DiagnosticsReceipt: Decodable {
        let id: String
    }

    // MARK: URLs

    /// Resolves a server-relative URL such as a stream or artwork path.
    public func url(for relative: String) -> URL? {
        if relative.hasPrefix("http://") || relative.hasPrefix("https://") { return URL(string: relative) }
        return URL(string: relative, relativeTo: baseURL.appending(path: "/"))?.absoluteURL
    }

    /// Artwork URL with a width hint for the server's image cache. Image endpoints are anonymous.
    public func imageURL(_ relative: String?, width: Int) -> URL? {
        guard let relative, !relative.isEmpty else { return nil }
        let separator = relative.contains("?") ? "&" : "?"
        return url(for: "\(relative)\(separator)w=\(width)")
    }

    /// The title's backdrop cut to 16:9 with its logo on it, lower left (the backdrop alone if it has no logo); nil
    /// without a backdrop. Drawn by the server, for the Top Shelf's wide cards.
    public func wideArtworkURL(_ artwork: Artwork, width: Int) -> URL? {
        guard let backdrop = artwork.backdrop, var parts = URLComponents(string: backdrop),
              parts.path.hasSuffix("/backdrop") else { return nil }
        parts.path = String(parts.path.dropLast("backdrop".count)) + "wide"
        return imageURL(parts.string, width: width)
    }

    // MARK: Transport

    /// What a path segment keeps as it is: what a path allows, less the slash that would end it, plus % for escapes
    /// already made.
    private static let segmentCharacters: CharacterSet = {
        var allowed = CharacterSet.urlPathAllowed
        allowed.remove("/")
        allowed.insert("%")
        return allowed
    }()

    /// Free text (a genre's name) as one segment of a path: its slashes, backslashes and percent signs encoded, so
    /// "Action/Adventure" stays one segment.
    static func pathSegment(_ text: String) -> String {
        text.replacingOccurrences(of: "%", with: "%25").replacingOccurrences(of: "/", with: "%2F").replacingOccurrences(of: "\\", with: "%5C")
    }

    func get<T: Decodable>(_ path: String, query: [URLQueryItem] = []) async throws -> T {
        let data = try await send("GET", path, query: query)
        return try decode(T.self, from: data)
    }

    func send<Body: Encodable>(_ method: String, _ path: String, body: Body) async throws -> Data {
        try await send(method, path, bodyData: JSONEncoder().encode(body))
    }

    /// `racesAlternate`: false for a GET that is slow by nature (a subtitle search), so waiting on it never sends the
    /// same work to the other address too.
    func send(_ method: String, _ path: String, query: [URLQueryItem] = [], bodyData: Data? = nil,
              contentType: String = "application/json", timeout: TimeInterval = 20, racesAlternate: Bool = true) async throws -> Data {
        let (data, http) = try await exchange(method, path, query: query, bodyData: bodyData, contentType: contentType, timeout: timeout,
                                              racesAlternate: racesAlternate)

        switch http.statusCode {
        case 204, 202:
            return Data()
        case 200..<300:
            // Unknown paths fall through to the web app's index.html; don't try to decode that.
            if let type = http.value(forHTTPHeaderField: "Content-Type"), type.contains("text/html") {
                throw FlexarrClientError.unexpectedResponse
            }
            return data
        case 401:
            onUnauthorized?()
            throw FlexarrClientError.unauthorized
        default:
            if http.statusCode == 409, let recording = RecordingInTheWay(problem: data) {
                throw FlexarrClientError.recordingInTheWay(recording)
            }
            if http.statusCode == 403, let problem = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                if problem["ownPin"] as? Bool == true { throw FlexarrClientError.ownPinWrong(message: Self.problemMessage(data)) }
                if problem["parentPin"] as? Bool == true { throw FlexarrClientError.parentPinNeeded(message: Self.problemMessage(data)) }
                if problem["kidsLock"] as? Bool == true { throw FlexarrClientError.kidsLocked(message: Self.problemMessage(data)) }
            }
            throw FlexarrClientError.http(status: http.statusCode, message: Self.problemMessage(data))
        }
    }

    private func request(_ method: String, _ path: String, query: [URLQueryItem], bodyData: Data?, at base: URL,
                         contentType: String = "application/json", timeout: TimeInterval = 20) -> URLRequest {
        // Each segment encoded as it stands, keeping %-escapes: a name made one segment by pathSegment (a genre's,
        // which can hold a slash) stays one, which appending(path:) would split or encode twice.
        let encoded = path.split(separator: "/", omittingEmptySubsequences: false)
            .map { String($0).addingPercentEncoding(withAllowedCharacters: Self.segmentCharacters) ?? String($0) }
            .joined(separator: "/")
        var url = URL(string: base.appending(path: "api/v1").absoluteString + "/" + encoded) ?? base.appending(path: "api/v1/\(path)")
        if !query.isEmpty { url.append(queryItems: query) }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = timeout
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let apiKey, !apiKey.isEmpty {
            request.setValue(apiKey, forHTTPHeaderField: "X-Api-Key")
        }
        if let profileToken, !profileToken.isEmpty {
            request.setValue(profileToken, forHTTPHeaderField: "X-Flexarr-Profile")
        }
        if let deviceType = Self.deviceType {
            request.setValue(deviceType, forHTTPHeaderField: "X-Flexarr-Device")
        }
        if let bodyData {
            request.httpBody = bodyData
            request.setValue(contentType, forHTTPHeaderField: "Content-Type")
        }
        return request
    }

    private typealias Answer = (data: Data, response: HTTPURLResponse)

    /// Sends the request to the address that answered last. With a second address, a GET that can't connect to the
    /// first (or hasn't heard back after `ServerAddresses.alternateDelay`) is also sent there and the first answer
    /// wins; anything else only moves over once the first address has failed to connect, so a write is never sent
    /// twice. Whichever address answers is used from then on.
    private func exchange(_ method: String, _ path: String, query: [URLQueryItem], bodyData: Data?,
                          contentType: String, timeout: TimeInterval, racesAlternate: Bool) async throws -> Answer {
        let primary = addresses.current
        let first = request(method, path, query: query, bodyData: bodyData, at: primary, contentType: contentType, timeout: timeout)
        guard let alternate = addresses.alternate(to: primary) else {
            return try await exchange(first)
        }

        let second = request(method, path, query: query, bodyData: bodyData, at: alternate, contentType: contentType, timeout: timeout)
        let answer: Answer
        let reached: URL
        if method == "GET", racesAlternate {
            (answer, reached) = try await race(first, at: primary, then: second, at: alternate)
        } else {
            do {
                answer = try await exchange(first)
                reached = primary
            } catch let error where Self.couldNotConnect(error) {
                guard let other = try? await exchange(second) else { throw FlexarrClientError.unreachable }
                answer = other
                reached = alternate
            }
        }
        addresses.reached(reached)
        checkHomeIfDue()
        return answer
    }

    private func exchange(_ request: URLRequest) async throws -> Answer {
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw FlexarrClientError.invalidResponse }
        return (data, http)
    }

    /// Both requests at once, the second starting after a moment (or at once, if the first can't connect). The first
    /// answer wins, and the other is cancelled. An answer that isn't a connection problem (the server refused it, say)
    /// from the first address ends it: the other address wouldn't say anything different.
    private func race(_ first: URLRequest, at primary: URL, then second: URLRequest, at alternate: URL) async throws -> (Answer, URL) {
        typealias Attempt = (url: URL, result: Result<Answer, Error>)
        let delay = Task { try? await Task.sleep(for: ServerAddresses.alternateDelay) }
        return try await withThrowingTaskGroup(of: Attempt.self) { group in
            group.addTask {
                let result = await Self.attempt { try await self.exchange(first) }
                if case .failure(let error) = result, Self.couldNotConnect(error) { delay.cancel() }
                return (primary, result)
            }
            group.addTask {
                _ = await delay.value
                if Task.isCancelled { return (alternate, .failure(CancellationError())) }
                return (alternate, await Self.attempt { try await self.exchange(second) })
            }

            for try await attempt in group {
                switch attempt.result {
                case .success(let answer):
                    group.cancelAll()
                    delay.cancel()
                    return (answer, attempt.url)
                case .failure(let error):
                    if attempt.url == primary, !Self.couldNotConnect(error) {
                        group.cancelAll()
                        delay.cancel()
                        throw error
                    }
                    // Couldn't connect (or the alternate was called off): wait for the other one.
                }
            }
            throw FlexarrClientError.unreachable
        }
    }

    private static func attempt<T: Sendable>(_ work: () async throws -> T) async -> Result<T, Error> {
        do {
            return .success(try await work())
        } catch {
            return .failure(error)
        }
    }

    /// The address didn't answer at all (as opposed to answering with an error or the request being cancelled).
    static func couldNotConnect(_ error: Error) -> Bool {
        guard let error = error as? URLError else { return false }
        switch error.code {
        case .cannotFindHost, .dnsLookupFailed, .cannotConnectToHost, .networkConnectionLost, .timedOut,
             .notConnectedToInternet, .dataNotAllowed, .internationalRoamingOff, .secureConnectionFailed,
             .appTransportSecurityRequiresSecureConnection:
            return true
        default:
            return false
        }
    }

    /// While away, sees every few minutes whether home answers again, and goes back to it when it does.
    private func checkHomeIfDue() {
        guard addresses.shouldCheckHome() else { return }
        var probe = request("GET", "system/status", query: [], bodyData: nil, at: addresses.home)
        probe.timeoutInterval = 5
        let session = session
        let addresses = addresses
        Task.detached {
            guard let (_, response) = try? await session.data(for: probe), (response as? HTTPURLResponse)?.statusCode == 200 else { return }
            addresses.reached(addresses.home)
        }
    }

    func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        do {
            return try Self.decoder.decode(T.self, from: data)
        } catch {
            throw FlexarrClientError.unexpectedResponse
        }
    }

    /// Reads ASP.NET problem details (`errors`, `detail` or `title`), or a plain `reason`, into one message.
    static func problemMessage(_ data: Data) -> String? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        if let errors = object["errors"] as? [String: [String]], let first = errors.values.flatMap({ $0 }).first {
            return first
        }
        return (object["detail"] as? String) ?? (object["title"] as? String) ?? (object["reason"] as? String)
    }

    public static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let value = try decoder.singleValueContainer().decode(String.self)
            if let date = FlexarrDate.parse(value) { return date }
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Invalid date: \(value)"))
        }
        return decoder
    }()
}
