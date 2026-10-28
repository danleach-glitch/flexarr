import Foundation

// MARK: - Models

/// Subtitles Bazarr found for a file, best first.
public struct SubtitleSearchResults: Codable, Sendable, Hashable {
    public let results: [SubtitleSearchResult]
}

/// One set of subtitles a provider has: pass its `id` to `downloadSubtitles` to fetch it.
public struct SubtitleSearchResult: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let provider: String
    public let language: String
    /// "English", "English (SDH)".
    public let languageLabel: String
    /// The release the subtitles were made for.
    public let release: String
    public let hearingImpaired: Bool
    public let forced: Bool
    /// 0 to 100: how well they match the file.
    public let score: Int
    /// Made for this very release, so they should be in time.
    public let matchesFile: Bool
    public let uploader: String?

    public init(id: String, provider: String, language: String, languageLabel: String, release: String, hearingImpaired: Bool,
                forced: Bool, score: Int, matchesFile: Bool, uploader: String?) {
        self.id = id
        self.provider = provider
        self.language = language
        self.languageLabel = languageLabel
        self.release = release
        self.hearingImpaired = hearingImpaired
        self.forced = forced
        self.score = score
        self.matchesFile = matchesFile
        self.uploader = uploader
    }
}

/// Subtitles saved beside the file: the track's key in the next playback session, and its label.
public struct DownloadedSubtitles: Codable, Sendable, Hashable {
    public let key: String
    public let label: String
}

/// How a re-sync of a file's subtitles to its audio is going.
public struct SubtitleSyncStatus: Codable, Sendable, Hashable {
    public enum State: String, ForwardCompatibleEnum {
        case running, done, failed
        /// Nothing has been re-synced for this file (since the server started).
        case idle = "none"
        case unknown
    }

    public let state: State
    /// The subtitle track being (or last) re-synced.
    public let key: String?
    /// Why it failed, in words.
    public let error: String?
}

struct SubtitleDownloadBody: Codable, Sendable {
    let result: String?
    let fileId: String
}

struct SubtitleSyncBody: Codable, Sendable {
    let key: String
    let fileId: String
}

// MARK: - Client

extension FlexarrClient {
    /// Asks Bazarr's providers for subtitles for a file. Slow: often a minute, sometimes more.
    public func searchSubtitles(itemId: String, fileId: String) async throws -> [SubtitleSearchResult] {
        let data = try await send("GET", "items/\(itemId)/subtitles/search", query: [.init(name: "fileId", value: fileId)],
                                  timeout: 180, racesAlternate: false)
        return try decode(SubtitleSearchResults.self, from: data).results
    }

    /// Saves a search result beside the file (`result` nil: the best match Bazarr can find). The new track has the
    /// returned key in the next playback session.
    public func downloadSubtitles(itemId: String, fileId: String, result: String? = nil) async throws -> DownloadedSubtitles {
        let body = try JSONEncoder().encode(SubtitleDownloadBody(result: result, fileId: fileId))
        let data = try await send("POST", "items/\(itemId)/subtitles/download", bodyData: body, timeout: 180)
        return try decode(DownloadedSubtitles.self, from: data)
    }

    /// Starts re-syncing a subtitle file to the video's audio; follow it with `subtitleSync`. A 409 means one is
    /// already running for this file.
    public func startSubtitleSync(itemId: String, fileId: String, key: String) async throws {
        _ = try await send("POST", "items/\(itemId)/subtitles/sync", body: SubtitleSyncBody(key: key, fileId: fileId))
    }

    public func subtitleSync(itemId: String, fileId: String) async throws -> SubtitleSyncStatus {
        try await get("items/\(itemId)/subtitles/sync", query: [.init(name: "fileId", value: fileId)])
    }
}
