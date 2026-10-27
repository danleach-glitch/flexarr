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

    public var errorDescription: String? {
        switch self {
        case .unauthorized:
            "The server didn’t accept this device’s sign-in. Connect it again with a code, or if you typed an API key, check it against Settings › Server on the web. Over Tailscale, your Tailscale account needs linking in Settings › Remote access."
        case .recordingInTheWay(let recording):
            recording.message
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

    public func person(_ id: String) async throws -> Person {
        try await get("people/\(id)")
    }

    public func search(_ query: String) async throws -> SearchResults {
        try await get("search", query: [.init(name: "q", value: query)])
    }

    public func startPlayback(_ request: PlaybackRequest) async throws -> PlaybackInfo {
        let data = try await send("POST", "playback", body: request)
        return try decode(PlaybackInfo.self, from: data)
    }

    public func reportProgress(sessionId: String, positionSeconds: Double, paused: Bool, ended: Bool = false) async throws {
        _ = try await send("POST", "playback/\(sessionId)/progress",
                           body: ProgressReport(positionSeconds: max(0, positionSeconds), paused: paused, ended: ended))
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

    // MARK: Profiles

    public func profiles() async throws -> [Profile] {
        try await get("profiles")
    }

    /// Checks the PIN (if the profile has one) and returns the token for `profileToken`.
    /// A wrong PIN is a 403 whose message says so.
    public func selectProfile(_ id: String, pin: String? = nil) async throws -> ProfileSelection {
        let data = try await send("POST", "profiles/\(id)/select", body: SelectProfileRequest(pin: pin))
        return try decode(ProfileSelection.self, from: data)
    }

    // MARK: Pairing (connecting without typing the API key)

    /// Asks the server for a code to show; someone signed in as the main profile approves it in the web app.
    public func startPairing(deviceName: String) async throws -> PairingStart {
        let data = try await send("POST", "pairing", body: ["deviceName": deviceName])
        return try decode(PairingStart.self, from: data)
    }

    /// Polled while the code is on screen; carries this Apple TV's own token once approved.
    public func pairingStatus(_ id: String) async throws -> PairingStatus {
        try await get("pairing/\(id)")
    }

    // MARK: Devices (playing on another screen)

    /// Every Apple TV, iPhone and iPad signed in to the server, the ones with Flexarr open first.
    public func devices() async throws -> [Device] {
        try await get("devices")
    }

    /// Tells a device to play a title. A show's or season's id plays the episode that device's profile is up to; the
    /// result says which, and whether the device took it (Flexarr must be open on it).
    public func play(on deviceId: String, itemId: String, fromBeginning: Bool = false, shuffle: Bool = false) async throws -> DeviceCommandResult {
        let data = try await send("POST", "devices/\(deviceId)/play", body: DevicePlayBody(itemId: itemId, fromBeginning: fromBeginning, shuffle: shuffle))
        return try decode(DeviceCommandResult.self, from: data)
    }

    /// Puts a Live TV channel on another device.
    public func watch(on deviceId: String, channelId: String) async throws -> DeviceCommandResult {
        let data = try await send("POST", "devices/\(deviceId)/watch", body: DeviceWatchBody(channelId: channelId))
        return try decode(DeviceCommandResult.self, from: data)
    }

    /// Pauses, resumes or stops what a device is playing.
    public func control(device deviceId: String, _ action: DeviceControl) async throws -> DeviceCommandResult {
        let data = try await send("POST", "devices/\(deviceId)/\(action.rawValue)")
        return try decode(DeviceCommandResult.self, from: data)
    }

    /// Commands sent to this device, as they arrive, over one long-lived request (server-sent events). Ends when the
    /// server closes it (a newer connection from this device took over) or the connection drops; the caller opens it
    /// again. Only a device signed in with its own token has one: with the API key the server answers 403.
    public func commandStream() -> AsyncThrowingStream<DeviceCommand, Error> {
        var request = URLRequest(url: baseURL.appending(path: "api/v1/devices/commands"))
        request.timeoutInterval = 90 // The server writes a comment every 20 seconds.
        request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        if let apiKey, !apiKey.isEmpty { request.setValue(apiKey, forHTTPHeaderField: "X-Api-Key") }
        if let profileToken, !profileToken.isEmpty { request.setValue(profileToken, forHTTPHeaderField: "X-Flexarr-Profile") }
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
                        guard let event = events.feed(text), event.name == "command" else { continue }
                        continuation.yield(try Self.decoder.decode(DeviceCommand.self, from: Data(event.data.utf8)))
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

    func get<T: Decodable>(_ path: String, query: [URLQueryItem] = []) async throws -> T {
        let data = try await send("GET", path, query: query)
        return try decode(T.self, from: data)
    }

    func send<Body: Encodable>(_ method: String, _ path: String, body: Body) async throws -> Data {
        try await send(method, path, bodyData: JSONEncoder().encode(body))
    }

    func send(_ method: String, _ path: String, query: [URLQueryItem] = [], bodyData: Data? = nil,
              contentType: String = "application/json", timeout: TimeInterval = 20) async throws -> Data {
        let (data, http) = try await exchange(method, path, query: query, bodyData: bodyData, contentType: contentType, timeout: timeout)

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
            throw FlexarrClientError.http(status: http.statusCode, message: Self.problemMessage(data))
        }
    }

    private func request(_ method: String, _ path: String, query: [URLQueryItem], bodyData: Data?, at base: URL,
                         contentType: String = "application/json", timeout: TimeInterval = 20) -> URLRequest {
        var url = base.appending(path: "api/v1/\(path)")
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
                          contentType: String, timeout: TimeInterval) async throws -> Answer {
        let primary = addresses.current
        let first = request(method, path, query: query, bodyData: bodyData, at: primary, contentType: contentType, timeout: timeout)
        guard let alternate = addresses.alternate(to: primary) else {
            return try await exchange(first)
        }

        let second = request(method, path, query: query, bodyData: bodyData, at: alternate, contentType: contentType, timeout: timeout)
        let answer: Answer
        let reached: URL
        if method == "GET" {
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
