import Foundation

// Swift mirrors of `frontend/src/api/types.ts`. JSON is camelCase, enums are strings and
// timestamps are ISO 8601 (see `FlexarrDate`). Calendar dates (`releaseDate`, `airDate`)
// stay as "yyyy-MM-dd" strings because they have no time zone.

// MARK: - Enums

/// A string enum that decodes unknown server values to `.unknown` instead of failing,
/// so a newer server never breaks an older app.
public protocol ForwardCompatibleEnum: RawRepresentable, Codable, Sendable, Hashable where RawValue == String {
    static var unknown: Self { get }
}

extension ForwardCompatibleEnum {
    public init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = Self(rawValue: raw) ?? .unknown
    }
}

public enum LibraryType: String, ForwardCompatibleEnum {
    case movies = "Movies", tvShows = "TvShows", music = "Music", unknown
}

public enum MediaKind: String, ForwardCompatibleEnum {
    case movie = "Movie", series = "Series", season = "Season", episode = "Episode", unknown
}

public enum VideoRange: String, ForwardCompatibleEnum {
    case sdr = "Sdr", hdr10 = "Hdr10", hlg = "Hlg", dolbyVision = "DolbyVision", unknown
}

public enum StreamType: String, ForwardCompatibleEnum {
    case video = "Video", audio = "Audio", subtitle = "Subtitle", unknown
}

public enum CreditRole: String, ForwardCompatibleEnum {
    case actor = "Actor", director = "Director", writer = "Writer", producer = "Producer", creator = "Creator", unknown
}

public enum PlaybackMethod: String, ForwardCompatibleEnum {
    case directPlay = "DirectPlay", transcode = "Transcode", unknown
}

public enum MarkerType: String, ForwardCompatibleEnum {
    case intro = "Intro", recap = "Recap", credits = "Credits", unknown
}

public enum HomeRowStyle: String, ForwardCompatibleEnum {
    case poster, wide, top10, unknown
}

// MARK: - System

public struct SystemStatus: Codable, Sendable, Equatable {
    public let appName: String
    public let version: String
    public let osDescription: String
    public let runtimeVersion: String
    public let startTime: Date
}

// MARK: - Libraries

public struct ScanStatus: Codable, Sendable, Hashable {
    public let path: String
    public let total: Int
    public let processed: Int
}

public struct Library: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let name: String
    public let type: LibraryType
    public let folders: [String]
    public let itemCount: Int
    public let lastScannedAt: Date?
    public let scan: ScanStatus?
}

public struct GenreCount: Codable, Sendable, Hashable {
    public let name: String
    public let count: Int
}

public struct Paged<T: Codable & Sendable>: Codable, Sendable {
    public let total: Int
    public let items: [T]
}

/// Sort orders accepted by `GET /libraries/{id}/items`.
public enum LibrarySort: String, CaseIterable, Sendable, Identifiable {
    case title = ""
    case added
    case year
    case rating

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .title: "Title"
        case .added: "Recently added"
        case .year: "Year"
        case .rating: "Rating"
        }
    }
}

// MARK: - Items

public struct Artwork: Codable, Sendable, Hashable {
    public let poster: String?
    public let backdrop: String?
    public let logo: String?
    public let thumb: String?

    public init(poster: String? = nil, backdrop: String? = nil, logo: String? = nil, thumb: String? = nil) {
        self.poster = poster
        self.backdrop = backdrop
        self.logo = logo
        self.thumb = thumb
    }
}

public struct UserData: Codable, Sendable, Hashable {
    public let positionSeconds: Double
    public let watched: Bool
    public let playCount: Int
    public let lastPlayedAt: Date?
    public let unwatchedCount: Int?
}

public struct ItemSummary: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let libraryId: String
    public let parentId: String?
    public let kind: MediaKind
    public let title: String
    public let year: Int?
    public let seasonNumber: Int?
    public let episodeNumber: Int?
    public let episodeNumberEnd: Int?
    public let airDate: String?
    public let childCount: Int
    public let durationSeconds: Double?
    public let resolution: String?
    public let addedAt: Date
    public let artwork: Artwork
    public let accentColor: String?
    public let communityRating: Double?
    public let criticRating: Double?
    public let contentRating: String?
    public let runtimeMinutes: Int?
    public let releaseDate: String?
    public let userData: UserData
    public let seriesTitle: String?
}

public struct MediaStream: Codable, Sendable, Hashable {
    public let index: Int
    public let type: StreamType
    public let codec: String
    public let profile: String?
    public let language: String?
    public let title: String?
    public let isDefault: Bool
    public let isForced: Bool
    public let width: Int?
    public let height: Int?
    public let channels: Int?
}

public struct MediaFile: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let path: String
    public let size: Int64
    public let container: String?
    public let durationSeconds: Double?
    public let bitrate: Int?
    public let videoCodec: String?
    public let width: Int?
    public let height: Int?
    public let videoRange: VideoRange
    public let audioCodec: String?
    public let audioChannels: Int?
    public let probeError: String?
    public let streams: [MediaStream]
}

public struct Credit: Codable, Sendable, Hashable {
    public let personId: String
    public let name: String
    public let role: CreditRole
    public let character: String?
    public let image: String?
}

public struct ItemDetail: Codable, Sendable {
    public let item: ItemSummary
    public let path: String?
    public let originalTitle: String?
    public let overview: String?
    public let studio: String?
    public let status: String?
    public let trailerYouTubeId: String?
    public let genres: [String]
    public let tvdbId: String?
    public let tmdbId: String?
    public let imdbId: String?
    public let ancestors: [ItemSummary]
    public let children: [ItemSummary]
    public let credits: [Credit]
    public let files: [MediaFile]
    /// Overviews of `children`, keyed by child id (seasons carry their episodes' overviews here).
    public let childOverviews: [String: String?]
}

public struct PersonCredit: Codable, Sendable, Hashable {
    public let item: ItemSummary
    public let role: CreditRole
    public let character: String?
}

public struct Person: Codable, Sendable, Identifiable {
    public let id: String
    public let name: String
    public let image: String?
    public let credits: [PersonCredit]
}

// MARK: - Home and search

public struct HomeRow: Codable, Sendable, Identifiable, Hashable {
    public let id: String
    public let title: String
    public let style: HomeRowStyle
    public let items: [ItemSummary]
    public let link: String?
}

public struct Home: Codable, Sendable {
    public let featured: [ItemSummary]
    public let overviews: [String: String?]
    public let rows: [HomeRow]

    public func overview(for id: String) -> String? {
        overviews[id] ?? nil
    }
}

public struct PersonSummary: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let name: String
    public let image: String?
    public let credits: Int
}

public struct SearchResults: Codable, Sendable {
    public let titles: [ItemSummary]
    public let episodes: [ItemSummary]
    public let people: [PersonSummary]
    public let genres: [String]

    public var isEmpty: Bool { titles.isEmpty && episodes.isEmpty && people.isEmpty }
}

// MARK: - Playback

public struct DeviceProfile: Codable, Sendable, Hashable {
    public var client: String
    public var containers: [String]
    public var videoCodecs: [String]
    public var audioCodecs: [String]
    public var hdr: Bool
    public var maxHeight: Int
    public var maxAudioChannels: Int
    public var maxBitrate: Int

    public init(client: String, containers: [String], videoCodecs: [String], audioCodecs: [String],
                hdr: Bool, maxHeight: Int, maxAudioChannels: Int, maxBitrate: Int) {
        self.client = client
        self.containers = containers
        self.videoCodecs = videoCodecs
        self.audioCodecs = audioCodecs
        self.hdr = hdr
        self.maxHeight = maxHeight
        self.maxAudioChannels = maxAudioChannels
        self.maxBitrate = maxBitrate
    }

    /// What AVPlayer on Apple TV can direct play. Anything else is remuxed/transcoded to HLS.
    public static func tvOS(hdr: Bool) -> DeviceProfile {
        DeviceProfile(
            client: "tvos",
            containers: ["mp4"],
            videoCodecs: ["h264", "hevc"],
            audioCodecs: ["aac", "ac3", "eac3", "mp3"],
            hdr: hdr,
            maxHeight: 2160,
            maxAudioChannels: 6,
            maxBitrate: 0
        )
    }

    /// What AVPlayer on iPhone and iPad can direct play: the same formats as Apple TV (AVPlayer mixes surround
    /// down for headphones and speakers, and passes it on over AirPlay).
    public static func iOS(hdr: Bool) -> DeviceProfile {
        var profile = tvOS(hdr: hdr)
        profile.client = "ios"
        return profile
    }
}

public struct PlaybackRequest: Codable, Sendable, Hashable {
    public var itemId: String
    public var profile: DeviceProfile
    public var fileId: String?
    public var audioStreamIndex: Int?
    public var subtitleKey: String?
    public var maxBitrate: Int?
    public var maxHeight: Int?
    /// Omit to let the server pick the resume point; 0 starts from the beginning.
    public var startPositionSeconds: Double?
    public var forceTranscode: Bool?
    /// This player's previous session, which the new one takes over from (only that one ends).
    public var replacesSessionId: String?
    /// With a show's id, a random episode starts; with an episode's, its `next` is a random one.
    public var shuffle: Bool?
    /// Getting the next episode ready during the credits: conversion starts now, and it never ends another screen.
    public var prepare: Bool?

    public init(itemId: String, profile: DeviceProfile, fileId: String? = nil, audioStreamIndex: Int? = nil,
                subtitleKey: String? = nil, maxBitrate: Int? = nil, maxHeight: Int? = nil,
                startPositionSeconds: Double? = nil, forceTranscode: Bool? = nil, replacesSessionId: String? = nil,
                shuffle: Bool? = nil) {
        self.shuffle = shuffle
        self.itemId = itemId
        self.profile = profile
        self.fileId = fileId
        self.audioStreamIndex = audioStreamIndex
        self.subtitleKey = subtitleKey
        self.maxBitrate = maxBitrate
        self.maxHeight = maxHeight
        self.startPositionSeconds = startPositionSeconds
        self.forceTranscode = forceTranscode
        self.replacesSessionId = replacesSessionId
    }
}

public struct AudioTrack: Codable, Sendable, Hashable {
    public let index: Int
    public let label: String
    public let codec: String
    public let language: String?
    public let channels: Int?
    public let isDefault: Bool
}

public struct SubtitleTrack: Codable, Sendable, Hashable {
    public let key: String
    public let label: String
    public let codec: String
    public let language: String?
    public let isForced: Bool
    public let isDefault: Bool
    public let isText: Bool
    public let url: String?
}

public struct Marker: Codable, Sendable, Hashable {
    public let type: MarkerType
    public let start: Double
    public let end: Double

    public init(type: MarkerType, start: Double, end: Double) {
        self.type = type
        self.start = start
        self.end = end
    }
}

public struct PlaybackInfo: Codable, Sendable {
    public let sessionId: String
    public let method: PlaybackMethod
    /// Relative to the server; resolve with `FlexarrClient.url(for:)`. Needs no API key.
    public let url: String
    public let reasons: [String]
    public let durationSeconds: Double
    /// Where to start: the resume point, or what the request asked for.
    public let startPositionSeconds: Double
    public let fileId: String
    public let audioTracks: [AudioTrack]
    public let selectedAudioIndex: Int?
    public let subtitles: [SubtitleTrack]
    public let selectedSubtitleKey: String?
    public let subtitleBurnedIn: Bool
    public let markers: [Marker]
    public let item: ItemSummary
    public let ancestors: [ItemSummary]
    public let next: ItemSummary?
    public let transcodeSummary: String?
    /// Playing a shuffle: `next` is a random episode. Absent from servers before shuffle.
    public let shuffle: Bool?
    /// The profile's auto-play and skip preferences. Absent from servers before they existed.
    public let preferences: PlaybackPreferences?
    /// This title's subtitles are shown this many seconds later (earlier when negative). The server shifts them.
    public var subtitleDelaySeconds: Double? = nil
}

/// How a profile likes to watch: auto-play the next episode, and what to skip without asking.
public struct PlaybackPreferences: Codable, Sendable, Hashable {
    public var autoPlayNext: Bool
    public var skipIntros: Bool
    public var skipRecaps: Bool
    public var skipCredits: Bool
    /// Absent from servers before subtitle sizes; left out when saving, the server keeps the profile's own.
    public var subtitleSize: SubtitleSize?

    public init(autoPlayNext: Bool = true, skipIntros: Bool = false, skipRecaps: Bool = false, skipCredits: Bool = false,
                subtitleSize: SubtitleSize? = nil) {
        self.autoPlayNext = autoPlayNext
        self.skipIntros = skipIntros
        self.skipRecaps = skipRecaps
        self.skipCredits = skipCredits
        self.subtitleSize = subtitleSize
    }

    /// What a profile starts with, and what's assumed from a server that doesn't say.
    public static let standard = PlaybackPreferences()
}

struct ProgressReport: Codable, Sendable {
    let positionSeconds: Double
    let paused: Bool
    let ended: Bool
}

struct WatchedRequest: Codable, Sendable {
    let watched: Bool
}

// MARK: - Pairing

/// A code for connecting this device: shown on screen (and as a QR code) for someone to approve.
public struct PairingStart: Decodable, Sendable, Equatable {
    /// The device's secret handle for polling; never shown.
    public let id: String
    /// What a person approves, e.g. "K7M-Q4X".
    public let code: String
    public let expiresAt: Date
}

public struct PairingStatus: Decodable, Sendable, Equatable {
    /// "waiting", "approved" or "expired".
    public let status: String
    /// Set once, when approved: this Apple TV's own token, sent as the API key from then on.
    public let apiKey: String?
}

/// What `/me` says about this client.
public struct SignInInfo: Decodable, Sendable, Equatable {
    public let profileId: String
    /// "api-key" (the server's key itself), "device" (this device's own token), "tailscale" or "local".
    public let method: String
    public let isAdmin: Bool
}

struct DeviceToken: Decodable, Sendable {
    let token: String
}

// MARK: - Profiles

/// A person in the household: their own watch history, Continue Watching and (optionally) a PIN.
public struct Profile: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let name: String
    /// "#a9c2ec"; the circle behind the initial when there's no picture.
    public let color: String
    public let hasPin: Bool
    public let isMain: Bool
    /// The profile the request that fetched this list acted for.
    public let isCurrent: Bool
    public let hiddenLibraryIds: [String]
    /// The picture's server-relative URL, or nil to show the initial on the colour.
    public let image: String?

    public init(id: String, name: String, color: String, hasPin: Bool = false, isMain: Bool = false,
                isCurrent: Bool = false, hiddenLibraryIds: [String] = [], image: String? = nil) {
        self.id = id
        self.name = name
        self.color = color
        self.hasPin = hasPin
        self.isMain = isMain
        self.isCurrent = isCurrent
        self.hiddenLibraryIds = hiddenLibraryIds
        self.image = image
    }

    /// The first letter, shown when there's no picture.
    public var initial: String {
        name.trimmingCharacters(in: .whitespaces).first.map { String($0).uppercased() } ?? "?"
    }
}

/// What choosing a profile returns: the token to send as `X-Flexarr-Profile` from then on.
public struct ProfileSelection: Decodable, Sendable, Equatable {
    public let token: String
    public let profile: Profile
}

struct SelectProfileRequest: Codable, Sendable {
    let pin: String?
}

// MARK: - Downloads

public enum DownloadQuality: String, Codable, Sendable, CaseIterable, Identifiable {
    case original = "Original", high = "High", medium = "Medium", small = "Small"

    public var id: String { rawValue }

    /// "Original", "High (1080p)".
    public var label: String {
        switch self {
        case .original: "Original"
        case .high: "High (1080p)"
        case .medium: "Medium (720p)"
        case .small: "Small (480p)"
        }
    }
}

public enum DownloadStatus: String, ForwardCompatibleEnum {
    case queued = "Queued", preparing = "Preparing", ready = "Ready", received = "Received", failed = "Failed", expired = "Expired", unknown
}

/// A size a title can be downloaded at, about how big it'll be, and whether it can be (an Original the phone
/// can't play can't).
public struct DownloadOption: Codable, Sendable, Hashable, Identifiable {
    public let quality: DownloadQuality
    public let estimatedBytes: Int64
    public let available: Bool

    public var id: DownloadQuality { quality }
}

/// A download on the server: being got ready, ready to fetch, or fetched; with what's needed to show and play the
/// title offline.
public struct OfflineDownload: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let itemId: String
    public let quality: DownloadQuality
    public let status: DownloadStatus
    public let progress: Double
    public let sizeBytes: Int64?
    public let error: String?
    public let item: ItemSummary
    public let markers: [Marker]
    public let durationSeconds: Double
    public let nextId: String?
}

struct DownloadRequestBody: Codable, Sendable {
    let itemId: String
    let quality: DownloadQuality
}

struct OfflineProgressBody: Codable, Sendable {
    let positionSeconds: Double
    let finished: Bool
    /// ISO 8601, as the server reads it.
    let playedAt: String
}

extension PlaybackInfo {
    /// A downloaded file played with no server session: its own markers for Skip Intro, and the next episode if
    /// that's downloaded too.
    public static func offline(file: URL, item: ItemSummary, markers: [Marker], durationSeconds: Double, startPositionSeconds: Double,
                               next: ItemSummary?, preferences: PlaybackPreferences?) -> PlaybackInfo {
        PlaybackInfo(sessionId: "offline-\(item.id)", method: .directPlay, url: file.absoluteString, reasons: [], durationSeconds: durationSeconds,
                     startPositionSeconds: startPositionSeconds, fileId: item.id, audioTracks: [], selectedAudioIndex: nil, subtitles: [],
                     selectedSubtitleKey: nil, subtitleBurnedIn: false, markers: markers, item: item, ancestors: [], next: next,
                     transcodeSummary: nil, shuffle: nil, preferences: preferences)
    }

    /// Played from a downloaded file rather than streamed.
    public var isOffline: Bool { sessionId.hasPrefix("offline-") }
}

// MARK: - Subtitles

/// How big subtitles are drawn: one setting per profile, followed by every device.
public enum SubtitleSize: String, ForwardCompatibleEnum, CaseIterable {
    case small = "Small", standard = "Standard", large = "Large", extraLarge = "ExtraLarge", unknown

    public static var allCases: [SubtitleSize] { [.small, .standard, .large, .extraLarge] }

    public var label: String {
        switch self {
        case .small: "Small"
        case .standard, .unknown: "Standard"
        case .large: "Large"
        case .extraLarge: "Extra large"
        }
    }

    /// Percent of the player's own size (as the web player scales its cues).
    public var percent: Int {
        switch self {
        case .small: 80
        case .standard, .unknown: 100
        case .large: 125
        case .extraLarge: 150
        }
    }
}

struct SubtitleDelayBody: Codable, Sendable {
    let seconds: Double
}
