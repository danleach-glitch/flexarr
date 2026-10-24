import Foundation

public enum FlexarrClientError: Error, Equatable, LocalizedError {
    /// The server rejected the API key (or one is required and none was sent).
    case unauthorized
    case http(status: Int, message: String?)
    /// The response was not what a Flexarr server sends (for example an HTML page).
    case unexpectedResponse
    case invalidResponse
    case invalidAddress

    public var errorDescription: String? {
        switch self {
        case .unauthorized:
            "The server didn’t accept this device. Enter the API key from Settings › Server on the web, or connect through Tailscale with a Tailscale account the server knows (Settings › Remote access)."
        case .http(let status, let message):
            message ?? "The server answered with an error (HTTP \(status))."
        case .unexpectedResponse:
            "That address answered, but not like a Flexarr server. Check the host name and port."
        case .invalidResponse:
            "The server sent a response that couldn't be read."
        case .invalidAddress:
            "That doesn't look like a server address. Try something like http://192.168.1.20:8383."
        }
    }
}

/// Talks to a Flexarr server's `/api/v1` REST API. Shared by the tvOS, iOS and macOS apps.
public struct FlexarrClient: Sendable {
    public let baseURL: URL
    public let apiKey: String?
    private let session: URLSession

    public init(baseURL: URL, apiKey: String? = nil, session: URLSession = .shared) {
        self.baseURL = baseURL
        self.apiKey = apiKey
        self.session = session
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

    public func item(_ id: String) async throws -> ItemDetail {
        try await get("items/\(id)")
    }

    public func similar(_ id: String) async throws -> [ItemSummary] {
        try await get("items/\(id)/similar")
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

    // MARK: Pairing (connecting without typing the API key)

    /// Asks the server for a code to show; someone signed in as the main profile approves it in the web app.
    public func startPairing(deviceName: String) async throws -> PairingStart {
        let data = try await send("POST", "pairing", body: ["deviceName": deviceName])
        return try decode(PairingStart.self, from: data)
    }

    /// Polled while the code is on screen; carries the API key once approved.
    public func pairingStatus(_ id: String) async throws -> PairingStatus {
        try await get("pairing/\(id)")
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

    // MARK: Transport

    func get<T: Decodable>(_ path: String, query: [URLQueryItem] = []) async throws -> T {
        let data = try await send("GET", path, query: query)
        return try decode(T.self, from: data)
    }

    private func send<Body: Encodable>(_ method: String, _ path: String, body: Body) async throws -> Data {
        try await send(method, path, bodyData: JSONEncoder().encode(body))
    }

    private func send(_ method: String, _ path: String, query: [URLQueryItem] = [], bodyData: Data? = nil) async throws -> Data {
        var url = baseURL.appending(path: "api/v1/\(path)")
        if !query.isEmpty { url.append(queryItems: query) }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = 20
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let apiKey, !apiKey.isEmpty {
            request.setValue(apiKey, forHTTPHeaderField: "X-Api-Key")
        }
        if let bodyData {
            request.httpBody = bodyData
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw FlexarrClientError.invalidResponse }

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
            throw FlexarrClientError.unauthorized
        default:
            throw FlexarrClientError.http(status: http.statusCode, message: Self.problemMessage(data))
        }
    }

    private func decode<T: Decodable>(_ type: T.Type, from data: Data) throws -> T {
        do {
            return try Self.decoder.decode(T.self, from: data)
        } catch {
            throw FlexarrClientError.unexpectedResponse
        }
    }

    /// Reads ASP.NET problem details (`errors`, `detail` or `title`) into one message.
    static func problemMessage(_ data: Data) -> String? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        if let errors = object["errors"] as? [String: [String]], let first = errors.values.flatMap({ $0 }).first {
            return first
        }
        return (object["detail"] as? String) ?? (object["title"] as? String)
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
