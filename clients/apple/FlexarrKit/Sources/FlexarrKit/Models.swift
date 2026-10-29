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
    case movies = "Movies", tvShows = "TvShows", music = "Music", podcasts = "Podcasts", homeVideos = "HomeVideos", unknown
}

public enum MediaKind: String, ForwardCompatibleEnum {
    case movie = "Movie", series = "Series", season = "Season", episode = "Episode"
    case artist = "Artist", album = "Album", track = "Track"
    case podcast = "Podcast", podcastEpisode = "PodcastEpisode", unknown

    /// An artist, album or track: listened to, not watched, so the music screens and player take it.
    public var isMusic: Bool {
        switch self {
        case .artist, .album, .track: true
        default: false
        }
    }

    /// A podcast or one of its episodes: these have screens and a player of their own (see `Podcast`).
    public var isPodcast: Bool { self == .podcast || self == .podcastEpisode }
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
    case poster, wide, top10
    /// The items are tracks (most played, say): shown as tracks that play, not titles that open.
    case tracks
    /// The profile's playlists; `items` is empty, the playlists come from `GET /playlists`.
    case playlists
    /// The profile's favourite TV channels with what's on; `items` is empty, the channels are in `channels`.
    case live
    case unknown
}

// MARK: - System

public struct SystemStatus: Codable, Sendable, Equatable {
    public let appName: String
    public let version: String
    public let osDescription: String
    public let runtimeVersion: String
    public let startTime: Date
    /// The server's address for away from home (over Tailscale), once remote access is turned on. Absent from
    /// servers older than 0.21.
    public var remoteUrl: URL?
    /// Whether the server's owner has turned on error reports (to Sentry) for the apps. Absent from older servers,
    /// which means off.
    public var errorReports: ErrorReportSetting?
}

/// The server's error-report switch, which the apps follow: off unless the owner turned it on.
public struct ErrorReportSetting: Codable, Sendable, Equatable {
    public var enabled: Bool
    /// A random id for the server, not tied to anything else: the only "user" an error report carries.
    public var installId: String?

    public init(enabled: Bool, installId: String? = nil) {
        self.enabled = enabled
        self.installId = installId
    }
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
    /// A backdrop from one of the library's titles in the genre. Absent from servers before 0.54.
    public var backdrop: String? = nil
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
    /// The show for episodes and seasons; the artist for tracks and albums.
    public let seriesTitle: String?
    /// Tracks only. Absent from servers before music.
    public var discNumber: Int? = nil
    public var trackNumber: Int? = nil
    /// The show's id for episodes and seasons, the artist's for tracks and albums. Absent from servers before it existed.
    public var seriesId: String? = nil
    /// Tracks only: the track's own artist when it isn't the album's (a compilation's tracks, a featured artist).
    public var trackArtist: String? = nil
    /// In a home video library: a "series" is a folder, its "seasons" the folders in it and its "episodes" videos,
    /// all known by name rather than number. Absent from servers before home videos.
    public var homeVideo: Bool? = nil
}

/// One line of a track's lyrics; `time` (seconds into the track) is set throughout synced lyrics and never otherwise.
public struct LyricLine: Codable, Sendable, Hashable {
    public let time: Double?
    public let text: String

    public init(time: Double?, text: String) {
        self.time = time
        self.text = text
    }
}

/// A track's lyrics: synced to the music when every line has a time, otherwise plain text.
public struct Lyrics: Codable, Sendable, Hashable {
    public let synced: Bool
    public let lines: [LyricLine]

    public init(synced: Bool, lines: [LyricLine]) {
        self.synced = synced
        self.lines = lines
    }
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
    /// HDR10+ scene-by-scene metadata on top of the HDR10. Absent from servers before 0.53.
    public var hdr10Plus: Bool? = nil
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
    /// Subtitles can be found for it through the server's Bazarr (a film or episode Sonarr or Radarr manage, with a
    /// file), for `files.first`, the copy playback picks. Absent from servers before 0.53.
    public var canSearchSubtitles: Bool? = nil
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
    /// A `playlists` row's content (`items` is empty then). Absent from servers before playlists.
    public var playlists: [PlaylistSummary]? = nil
    /// A `live` row's channels (`items` is empty then). Absent from servers before Live TV.
    public var channels: [LiveChannel]? = nil

    /// Whether there's anything to show: a row of items, of playlists or of channels. A row of none would be a bare heading.
    public var hasContent: Bool { !items.isEmpty || !(playlists ?? []).isEmpty || !(channels ?? []).isEmpty }
}

// MARK: - Playlists

/// One of the profile's music playlists, as a list shows it.
public struct PlaylistSummary: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let name: String
    public let trackCount: Int
    public let durationSeconds: Double
    /// Up to four of its tracks' album covers, first tracks first, for a mosaic.
    public let covers: [String]
    /// A cover chosen for it, shown instead of the mosaic.
    public let cover: String?
    public let createdAt: Date
    public let updatedAt: Date

    /// "12 tracks".
    public var trackCountLabel: String { trackCount == 1 ? "1 track" : "\(trackCount) tracks" }
}

/// A place in a playlist: the entry (what's moved or removed) and the track in it.
public struct PlaylistTrack: Codable, Sendable, Hashable, Identifiable {
    public let entryId: Int
    public let track: ItemSummary

    public var id: Int { entryId }
}

public struct PlaylistDetail: Codable, Sendable {
    public let summary: PlaylistSummary
    public let tracks: [PlaylistTrack]
}

public struct Home: Codable, Sendable {
    public let featured: [ItemSummary]
    public let overviews: [String: String?]
    public let rows: [HomeRow]
    /// What the first featured title resumes (the episode, for a show), when it's from Continue Watching.
    public var resume: ItemSummary? = nil
    /// What each part-played featured title resumes, by its id. Absent from servers before 0.53.
    public var resumes: [String: ItemSummary]? = nil

    public func overview(for id: String) -> String? {
        overviews[id] ?? nil
    }

    /// What a featured title's Play picks up from, when it's been started: the film itself, or a show's episode.
    public func resuming(_ item: ItemSummary) -> ItemSummary? {
        if let resumes { return resumes[item.id] }
        return item.id == featured.first?.id ? resume : nil
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
    /// Converted streams come as fragmented MP4, which carries lossless FLAC and Dolby Atmos (servers before 0.17
    /// ignore it and send MPEG-TS).
    public var fmp4: Bool?
    /// The display shows Dolby Vision (so profile 5, which has no HDR10 underneath, can be sent as it is).
    public var dolbyVision: Bool?
    /// The display shows HDR10 and HLG as they are right now (an Apple TV on a fixed Dolby Vision output doesn't).
    public var hdr10: Bool?
    public var hlg: Bool?
    /// The player demuxes the file itself (AetherEngine): any audio track, and picture subtitles drawn by it.
    public var selectsTracksItself: Bool?
    /// The player can play a file's sound later (`delaysAudio`) or earlier (`advancesAudio`) than its picture by itself,
    /// so the server leaves a file's audio offset to it. Otherwise the server's stream moves the sound. Servers before
    /// 0.53 ignore them.
    public var delaysAudio: Bool? = nil
    public var advancesAudio: Bool? = nil

    public init(client: String, containers: [String], videoCodecs: [String], audioCodecs: [String],
                hdr: Bool, maxHeight: Int, maxAudioChannels: Int, maxBitrate: Int, fmp4: Bool? = nil, dolbyVision: Bool? = nil,
                hdr10: Bool? = nil, hlg: Bool? = nil, selectsTracksItself: Bool? = nil) {
        self.client = client
        self.containers = containers
        self.videoCodecs = videoCodecs
        self.audioCodecs = audioCodecs
        self.hdr = hdr
        self.maxHeight = maxHeight
        self.maxAudioChannels = maxAudioChannels
        self.maxBitrate = maxBitrate
        self.fmp4 = fmp4
        self.dolbyVision = dolbyVision
        self.hdr10 = hdr10
        self.hlg = hlg
        self.selectsTracksItself = selectsTracksItself
    }

    /// What AVPlayer on Apple TV can direct play. Anything else comes as HLS in fragmented MP4: H.264 and HEVC video as
    /// it is (HDR and Dolby Vision kept), Dolby Digital Plus with its Atmos, and TrueHD, DTS and the like as lossless
    /// FLAC, up to 7.1.
    public static func tvOS(hdr: Bool, dolbyVision: Bool = false, hdr10: Bool? = nil, hlg: Bool? = nil) -> DeviceProfile {
        DeviceProfile(
            client: "tvos",
            containers: ["mp4"],
            videoCodecs: ["h264", "hevc"],
            audioCodecs: ["aac", "ac3", "eac3", "mp3", "flac", "alac"],
            hdr: hdr,
            maxHeight: 2160,
            maxAudioChannels: 8,
            maxBitrate: 0,
            fmp4: true,
            dolbyVision: dolbyVision,
            hdr10: hdr10,
            hlg: hlg
        )
    }

    /// What AetherEngine plays as it is on the device (experimental): the common containers, every codec it decodes
    /// in hardware or software, and every audio codec it carries, so the server sends the file rather than a stream.
    public static func engine(client: String, hdr: Bool, dolbyVision: Bool, hdr10: Bool?, hlg: Bool?) -> DeviceProfile {
        var profile = DeviceProfile(
            client: client,
            containers: ["mp4", "m4v", "mov", "mkv", "webm", "avi", "mpegts", "ts", "wmv", "flv"],
            videoCodecs: ["h264", "hevc", "av1", "vp9", "mpeg4", "mpeg2video", "vc1", "msmpeg4v3", "wmv3", "h263"],
            audioCodecs: ["aac", "ac3", "eac3", "mp3", "mp2", "flac", "alac", "truehd", "dts", "opus", "vorbis", "pcm_s16le", "pcm_s24le", "pcm_s32le", "wmav2", "wmapro"],
            hdr: hdr,
            maxHeight: 2160,
            maxAudioChannels: 8,
            maxBitrate: 0,
            fmp4: true,
            dolbyVision: dolbyVision,
            hdr10: hdr10,
            hlg: hlg,
            selectsTracksItself: true
        )
        // The engine moves the sound either way itself (up to two seconds), so the file comes as it is.
        profile.delaysAudio = true
        profile.advancesAudio = true
        return profile
    }

    /// What AVPlayer on iPhone and iPad can direct play: the same formats as Apple TV (AVPlayer mixes surround
    /// down for headphones and speakers, and passes it on over AirPlay).
    public static func iOS(hdr: Bool, dolbyVision: Bool = false, hdr10: Bool? = nil, hlg: Bool? = nil) -> DeviceProfile {
        var profile = tvOS(hdr: hdr, dolbyVision: dolbyVision, hdr10: hdr10, hlg: hlg)
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
    /// A subtitle file beside the video (rather than one inside it): only these can be re-synced. Absent from servers
    /// before 0.53.
    public var isExternal: Bool? = nil
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
    /// The episode before this one, in order: nil on shuffle, for the first episode, and from servers before it existed.
    public var previous: ItemSummary? = nil
    /// A short one-way hash of `sessionId`, for logs: the same playback can be found in the app's and the server's
    /// logs without either holding the session id, which is the stream URLs' credential. Absent from older servers.
    public var playbackId: String? = nil
    /// The file's sound is out of sync by this much: played this many seconds later (earlier when negative), for the
    /// whole household. Absent from servers before 0.53 (which can't move it).
    public var audioDelaySeconds: Double? = nil
    /// The server's stream already has the sound moved by `audioDelaySeconds`, so the player mustn't move it again.
    /// Absent from servers before 0.53.
    public var audioDelayInStream: Bool? = nil
    /// The speed this profile watches the show at (nil: normal). Absent from servers before 0.53.
    public var speed: Double? = nil

    /// The server keeps audio offsets and show speeds (0.53 and later).
    public var hasPlayerControls: Bool { audioDelaySeconds != nil }

    /// The server can find subtitles for this title (through Bazarr), and re-sync the ones beside its file. Absent from
    /// servers before 0.53.
    public var canSearchSubtitles: Bool? = nil
}

/// A file's scrubbing previews: sheets of `columns` × `rows` thumbnails, one every `interval` seconds.
public struct TrickplayManifest: Codable, Sendable, Hashable {
    public let interval: Int
    public let width: Int
    public let height: Int
    public let columns: Int
    public let rows: Int
    public let count: Int
    public let sheets: Int

    public init(interval: Int, width: Int, height: Int, columns: Int, rows: Int, count: Int, sheets: Int) {
        self.interval = interval
        self.width = width
        self.height = height
        self.columns = columns
        self.rows = rows
        self.count = count
        self.sheets = sheets
    }

    /// Which sheet holds the thumbnail for `seconds`, and its top-left corner on it (in pixels; each is `width` × `height`).
    public func thumbnail(at seconds: Double) -> (sheet: Int, x: Int, y: Int)? {
        guard interval > 0, columns > 0, rows > 0, count > 0, seconds.isFinite else { return nil }
        let index = min(count - 1, max(0, Int(seconds) / interval))
        let perSheet = columns * rows
        let cell = index % perSheet
        return (index / perSheet, (cell % columns) * width, (cell / columns) * height)
    }
}

/// How a profile likes to watch: auto-play the next episode, and what to skip without asking.
public struct PlaybackPreferences: Codable, Sendable, Hashable {
    public var autoPlayNext: Bool
    public var skipIntros: Bool
    public var skipRecaps: Bool
    public var skipCredits: Bool
    /// Absent from servers before subtitle sizes; left out when saving, the server keeps the profile's own.
    public var subtitleSize: SubtitleSize?
    /// How high subtitles sit over the picture. Absent from servers before 0.53; left out when saving, it's kept.
    public var subtitlePosition: SubtitlePosition?
    /// In films wider than the screen, subtitles go in the black bar below the picture. Absent from servers before 0.53.
    public var subtitlesInBlackBars: Bool?

    public init(autoPlayNext: Bool = true, skipIntros: Bool = false, skipRecaps: Bool = false, skipCredits: Bool = false,
                subtitleSize: SubtitleSize? = nil, subtitlePosition: SubtitlePosition? = nil, subtitlesInBlackBars: Bool? = nil) {
        self.autoPlayNext = autoPlayNext
        self.skipIntros = skipIntros
        self.skipRecaps = skipRecaps
        self.skipCredits = skipCredits
        self.subtitleSize = subtitleSize
        self.subtitlePosition = subtitlePosition
        self.subtitlesInBlackBars = subtitlesInBlackBars
    }

    /// Where this profile wants subtitles drawn.
    public var subtitlePlacement: SubtitlePlacement {
        SubtitlePlacement(position: subtitlePosition ?? .bottom, inBlackBars: subtitlesInBlackBars ?? false)
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

/// GET /setup: a new server that nobody has set up in a browser yet.
public struct SetupStatus: Decodable, Sendable, Equatable {
    public let open: Bool
    /// Setting up asks for the code in the server's log. Absent from servers before 0.52, which don't.
    public var codeRequired: Bool? = nil
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
    /// The order of the libraries in every app's menu: library ids, and "live-tv" for Live TV's place. Empty, by
    /// name. Absent from servers before 0.54, which keep no order: the Apple TV then keeps its own.
    public var libraryOrder: [String]? = nil

    public init(id: String, name: String, color: String, hasPin: Bool = false, isMain: Bool = false,
                isCurrent: Bool = false, hiddenLibraryIds: [String] = [], image: String? = nil, libraryOrder: [String]? = nil) {
        self.id = id
        self.name = name
        self.color = color
        self.hasPin = hasPin
        self.isMain = isMain
        self.isCurrent = isCurrent
        self.hiddenLibraryIds = hiddenLibraryIds
        self.image = image
        self.libraryOrder = libraryOrder
    }

    /// The first letter, shown when there's no picture.
    public var initial: String {
        name.trimmingCharacters(in: .whitespaces).first.map { String($0).uppercased() } ?? "?"
    }
}

public enum LibraryOrder {
    /// Live TV's place in a profile's library order.
    public static let liveTv = "live-tv"

    /// The profile's order after the `shown` entries were put in a new order. Entries this app doesn't show (Live TV
    /// on iPhone, a library hidden from it) keep the places they had; the shown ones fill the rest in their new order,
    /// and any the saved order didn't have yet follow.
    public static func reordered(saved: [String], shown: [String]) -> [String] {
        let showing = Set(shown)
        var next = shown[...]
        let order = saved.compactMap { showing.contains($0) ? next.popFirst() : $0 }
        return order + next
    }

    /// `entries` in the order of `saved`; any it doesn't mention, such as a new library, after them as they came.
    public static func sorted<T>(_ entries: [T], by saved: [String], id: (T) -> String) -> [T] {
        let rank = Dictionary(saved.enumerated().map { ($1.lowercased(), $0) }, uniquingKeysWith: { first, _ in first })
        return entries.enumerated()
            .sorted { (rank[id($0.element).lowercased()] ?? saved.count + $0.offset) < (rank[id($1.element).lowercased()] ?? saved.count + $1.offset) }
            .map(\.element)
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

public enum DownloadQuality: String, ForwardCompatibleEnum, Identifiable {
    case original = "Original", high = "High", medium = "Medium", small = "Small"
    /// A quality from a newer server: listed, but never asked for.
    case unknown

    public var id: String { rawValue }

    /// "Original", "High (1080p)".
    public var label: String {
        switch self {
        case .original: "Original"
        case .high: "High (1080p)"
        case .medium: "Medium (720p)"
        case .small: "Small (480p)"
        case .unknown: "Other"
        }
    }
}

public enum DownloadStatus: String, ForwardCompatibleEnum {
    case queued = "Queued", preparing = "Preparing", ready = "Ready", received = "Received", failed = "Failed", expired = "Expired"
    /// Deleted from the phone: a podcast episode the server won't offer for auto-download again.
    case removed = "Removed"
    case unknown
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
    /// What the file comes as once it's ready: "….mp4", or a track's "….mp3", "….flac" or "….m4a". Absent from
    /// servers before music downloads, which only sent MP4s.
    public var fileName: String? = nil

    /// The extension the file should be kept under, so the device's player opens it: "mp4" unless the server says.
    public var fileExtension: String {
        let ext = (fileName as NSString?)?.pathExtension ?? ""
        return ext.isEmpty ? "mp4" : ext
    }
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

// MARK: - Devices

/// What a device last reported playing.
public struct NowPlaying: Codable, Sendable, Hashable {
    public let item: ItemSummary
    public let positionSeconds: Double
    public let durationSeconds: Double
    public let paused: Bool
}

/// An Apple TV, iPhone or iPad signed in to the server, as a place to play something.
public struct Device: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let name: String
    /// Flexarr is open on it and would take a command now. Offline, a command waits two minutes for it.
    public let online: Bool
    public let lastUsedAt: Date
    public let profileId: String
    public let profileName: String?
    /// The device asking.
    public let isThisOne: Bool
    public let nowPlaying: NowPlaying?

    public init(id: String, name: String, online: Bool, lastUsedAt: Date, profileId: String, profileName: String?, isThisOne: Bool, nowPlaying: NowPlaying?) {
        self.id = id
        self.name = name
        self.online = online
        self.lastUsedAt = lastUsedAt
        self.profileId = profileId
        self.profileName = profileName
        self.isThisOne = isThisOne
        self.nowPlaying = nowPlaying
    }
}

public enum DeviceCommandKind: String, ForwardCompatibleEnum {
    case play = "Play", pause = "Pause", resume = "Resume", stop = "Stop", watch = "Watch", unknown
}

/// Something another device, the web app or an assistant asked this device to do.
public struct DeviceCommand: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let kind: DeviceCommandKind
    /// With `play`, the film, episode or track to start (never a show: the server has worked out the episode).
    public let itemId: String?
    public let fromBeginning: Bool
    /// Who asked ("Dan's iPhone"), when known.
    public let sentBy: String?
    public let sentAt: Date
    /// With `play` and a show as the item: its episodes in a random order.
    public let shuffle: Bool
    /// With `watch`, the Live TV channel to put on.
    public let channelId: String?
    /// With `play`, the profile to play it as: switch to it first (asking for its PIN, if it has one). Absent from
    /// servers before 0.54, and when it's whoever is watching here.
    public let profileId: String?

    public init(id: String, kind: DeviceCommandKind, itemId: String?, fromBeginning: Bool, sentBy: String?, sentAt: Date, shuffle: Bool = false,
                channelId: String? = nil, profileId: String? = nil) {
        self.id = id
        self.kind = kind
        self.itemId = itemId
        self.fromBeginning = fromBeginning
        self.sentBy = sentBy
        self.sentAt = sentAt
        self.shuffle = shuffle
        self.channelId = channelId
        self.profileId = profileId
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        id = try container.decode(String.self, forKey: .id)
        kind = try container.decode(DeviceCommandKind.self, forKey: .kind)
        itemId = try container.decodeIfPresent(String.self, forKey: .itemId)
        fromBeginning = try container.decodeIfPresent(Bool.self, forKey: .fromBeginning) ?? false
        sentBy = try container.decodeIfPresent(String.self, forKey: .sentBy)
        sentAt = try container.decode(Date.self, forKey: .sentAt)
        shuffle = try container.decodeIfPresent(Bool.self, forKey: .shuffle) ?? false // servers before 0.23 don't send it
        channelId = try container.decodeIfPresent(String.self, forKey: .channelId)
        profileId = try container.decodeIfPresent(String.self, forKey: .profileId)
    }
}

/// What came of telling a device to do something.
public struct DeviceCommandResult: Codable, Sendable, Hashable {
    /// The device took it. False: Flexarr isn't open on it; the command waits two minutes for it to be opened.
    public let delivered: Bool
    public let deviceName: String
    /// For play: what will play (the episode, when a show was asked for).
    public let item: ItemSummary?
    /// For watch: the channel being put on.
    public let channel: String?
}

/// Pause, resume or stop, for `FlexarrClient.control(device:_:)`.
public enum DeviceControl: String, Sendable {
    case pause, resume, stop
}

struct DeviceWatchBody: Codable, Sendable {
    let channelId: String
}

struct DevicePlayBody: Codable, Sendable {
    let itemId: String
    let fromBeginning: Bool
    let shuffle: Bool
    /// Left out when nil: servers before 0.54 don't know it.
    var profileId: String?
}

// MARK: - Subtitles

/// Where subtitles sit: at the foot of the picture, raised a little or a lot, or at the top.
public enum SubtitlePosition: String, ForwardCompatibleEnum, CaseIterable {
    case bottom = "Bottom", raised = "Raised", higher = "Higher", top = "Top", unknown

    public static var allCases: [SubtitlePosition] { [.bottom, .raised, .higher, .top] }

    public var label: String {
        switch self {
        case .bottom, .unknown: "Bottom"
        case .raised: "Raised"
        case .higher: "Higher"
        case .top: "Top"
        }
    }

    /// How far above the picture's foot the bottom of a subtitle sits, as a share of the picture's height (as the web
    /// player places them); nil at the top.
    public var lift: Double? {
        switch self {
        case .bottom, .unknown: 0.08
        case .raised: 0.16
        case .higher: 0.26
        case .top: nil
        }
    }
}

/// Where a profile wants subtitles: its position, and whether wide films put them in the black bar below the picture.
public struct SubtitlePlacement: Sendable, Hashable {
    public var position: SubtitlePosition
    public var inBlackBars: Bool

    public init(position: SubtitlePosition = .bottom, inBlackBars: Bool = false) {
        self.position = position
        self.inBlackBars = inBlackBars
    }
}

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

struct AudioDelayBody: Codable, Sendable {
    let seconds: Double
}

struct PlaybackSpeedBody: Codable, Sendable {
    let speed: Double
}
