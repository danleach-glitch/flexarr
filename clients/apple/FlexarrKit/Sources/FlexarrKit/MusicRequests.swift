import Foundation

// Music the household doesn't have, from Deezer, requested through Lidarr. All of it is absent from servers before
// music requests: they answer `musicRequestStatus()` with the web app's page (or a 404), read as off, and nothing else
// is asked then.

// MARK: - Models

/// Whether music requests are on for this profile: a Lidarr is connected and answered, and the profile sees music.
/// Kids and age-limited profiles always get `configured: false`.
public struct MusicRequestStatus: Codable, Sendable, Hashable {
    public let configured: Bool
    public let reachable: Bool

    public init(configured: Bool, reachable: Bool) {
        self.configured = configured
        self.reachable = reachable
    }

    public static let off = MusicRequestStatus(configured: false, reachable: false)
}

/// An artist or album from outside the library, with whether the household has it.
public struct MusicExternal: Codable, Sendable, Hashable, Identifiable {
    /// "artist" or "album".
    public let kind: String
    /// Deezer's id; 0 for an album only Lidarr knows (requested by `musicBrainzId`).
    public let deezerId: Int
    public let title: String
    /// An album's artist.
    public let artist: String?
    public let artistDeezerId: Int?
    public let year: Int?
    public let image: String?
    public let availability: Availability
    /// Set when the library has it: it opens as itself.
    public let libraryItemId: String?
    /// An album's kind on Deezer: "album", "ep", "single" or "compile".
    public var recordType: String? = nil
    /// An album's MusicBrainz release group, when Lidarr listed it.
    public var musicBrainzId: String? = nil

    public init(kind: String, deezerId: Int, title: String, artist: String? = nil, artistDeezerId: Int? = nil, year: Int? = nil,
                image: String? = nil, availability: Availability = .none, libraryItemId: String? = nil,
                recordType: String? = nil, musicBrainzId: String? = nil) {
        self.kind = kind
        self.deezerId = deezerId
        self.title = title
        self.artist = artist
        self.artistDeezerId = artistDeezerId
        self.year = year
        self.image = image
        self.availability = availability
        self.libraryItemId = libraryItemId
        self.recordType = recordType
        self.musicBrainzId = musicBrainzId
    }

    public var isArtist: Bool { kind == "artist" }
    public var isAlbum: Bool { kind == "album" }

    public var id: String { "\(kind)-\(deezerId == 0 ? (musicBrainzId ?? title) : String(deezerId))" }

    /// Whether it has an outside page to open (Lidarr-only albums don't).
    public var hasPage: Bool { deezerId != 0 }

    /// Whether a request can still be made: not in the library, not asked for, and something to ask by.
    public var isRequestable: Bool {
        availability == .none && (deezerId != 0 || musicBrainzId != nil)
    }
}

/// One of Discover › Music's rows.
public struct MusicRow: Codable, Sendable, Hashable, Identifiable {
    public let id: String
    public let title: String
    /// "artists" (round photos) or "albums" (square covers).
    public let shape: String
    public let items: [MusicExternal]

    public var isArtists: Bool { shape == "artists" }
}

public struct MusicGenre: Codable, Sendable, Hashable, Identifiable {
    public let id: Int
    public let name: String
    public let image: String?
}

public struct MusicDiscover: Codable, Sendable, Hashable {
    public let rows: [MusicRow]
    public let genres: [MusicGenre]

    public static let empty = MusicDiscover(rows: [], genres: [])
}

public struct MusicSearchResults: Codable, Sendable, Hashable {
    public let artists: [MusicExternal]
    public let albums: [MusicExternal]

    public static let empty = MusicSearchResults(artists: [], albums: [])
    public var isEmpty: Bool { artists.isEmpty && albums.isEmpty }
}

public struct MusicGenreDetail: Codable, Sendable, Hashable {
    public let id: Int
    public let name: String
    public let artists: [MusicExternal]
}

public struct MusicTrack: Codable, Sendable, Hashable {
    public let title: String
    public let durationSeconds: Int
    public let number: Int
    public let disc: Int
    public let explicit: Bool
}

/// An artist the household may not have: their releases, artists like them, and whether Lidarr follows them.
public struct MusicArtistDetail: Codable, Sendable, Hashable {
    public let deezerId: Int
    public let name: String
    public let image: String?
    /// Deezer's followers.
    public let fans: Int
    public let albums: [MusicExternal]
    public let similar: [MusicExternal]
    public let followed: Bool
    public let availability: Availability
    public let libraryItemId: String?
}

public struct MusicAlbumDetail: Codable, Sendable, Hashable {
    public let deezerId: Int
    public let title: String
    public let artist: String
    public let artistDeezerId: Int?
    public let image: String?
    public let year: Int?
    public let recordType: String?
    public let genres: [String]
    public let tracks: [MusicTrack]
    public let durationSeconds: Int
    public let availability: Availability
    public let libraryItemId: String?

    /// The album as rows know it, for its request button.
    public var summary: MusicExternal {
        MusicExternal(kind: "album", deezerId: deezerId, title: title, artist: artist, artistDeezerId: artistDeezerId, year: year,
                      image: image, availability: availability, libraryItemId: libraryItemId, recordType: recordType)
    }
}

/// A library artist's page from outside: their albums the library lacks, artists like them, and whether they're followed.
public struct MusicArtistExtras: Codable, Sendable, Hashable {
    public let missing: [MusicExternal]
    public let similar: [MusicExternal]
    public let followed: Bool
    /// The artist on Deezer, for their outside page; nil when Deezer doesn't know them.
    public let deezerId: Int?

    public static let empty = MusicArtistExtras(missing: [], similar: [], followed: false, deezerId: nil)
}

/// What a music request asks for: one album, or an artist's new releases.
public enum MusicRequestTarget: Sendable, Hashable {
    case album(deezerId: Int)
    /// An album only Lidarr lists, by its MusicBrainz release group.
    case albumByMusicBrainz(String)
    case artist(deezerId: Int)
    /// A library artist, by its library item id.
    case libraryArtist(String)

    /// An album from a row: by Deezer id, or MusicBrainz's when Deezer doesn't have it.
    public static func album(_ album: MusicExternal) -> MusicRequestTarget? {
        if album.deezerId != 0 { return .album(deezerId: album.deezerId) }
        if let mbid = album.musicBrainzId { return .albumByMusicBrainz(mbid) }
        return nil
    }

    var body: MusicRequestBody {
        switch self {
        case .album(let id): MusicRequestBody(kind: "album", deezerId: id)
        case .albumByMusicBrainz(let mbid): MusicRequestBody(kind: "album", musicBrainzId: mbid)
        case .artist(let id): MusicRequestBody(kind: "artist", deezerId: id)
        case .libraryArtist(let id): MusicRequestBody(kind: "artist", artistId: id)
        }
    }
}

struct MusicRequestBody: Encodable, Equatable {
    let kind: String
    var deezerId: Int? = nil
    var musicBrainzId: String? = nil
    var artistId: String? = nil
}

// MARK: - Endpoints

extension FlexarrClient {
    /// Whether music can be requested. Servers before music requests answer with the web app's page (200 text/html) or a
    /// 404; anything that isn't a status reads as off.
    public func musicRequestStatus() async throws -> MusicRequestStatus {
        do {
            return try await get("requests/music/status")
        } catch FlexarrClientError.unexpectedResponse {
            return .off
        } catch FlexarrClientError.http(status: 404, _) {
            return .off
        }
    }

    /// Discover › Music: rows and genre tiles.
    public func musicDiscover() async throws -> MusicDiscover {
        try await get("requests/music/discover")
    }

    /// A genre's artists.
    public func musicGenre(_ id: Int) async throws -> MusicGenreDetail {
        try await get("requests/music/genres/\(id)")
    }

    /// Artists and albums beyond the library, marked if the household has them.
    public func searchMusic(_ query: String) async throws -> MusicSearchResults {
        try await get("requests/music/search", query: [.init(name: "q", value: query)])
    }

    public func musicArtist(deezerId: Int) async throws -> MusicArtistDetail {
        try await get("requests/music/artist/\(deezerId)")
    }

    public func musicAlbum(deezerId: Int) async throws -> MusicAlbumDetail {
        try await get("requests/music/album/\(deezerId)")
    }

    /// A library artist's missing albums, artists like them and whether they're followed.
    public func musicArtistExtras(libraryItemId: String) async throws -> MusicArtistExtras {
        try await get("requests/music/library-artist/\(libraryItemId)")
    }

    /// Asks Lidarr for an album, or to follow an artist; the server remembers who asked so they hear when it's in.
    public func requestMusic(_ target: MusicRequestTarget) async throws {
        _ = try await send("POST", "requests/music", body: target.body)
    }
}
