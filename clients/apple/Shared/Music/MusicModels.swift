import FlexarrKit
import Foundation
import Observation

/// What a music library opens to: its artists (paged, by name) and the albums that arrived most recently. Shared by
/// the Apple TV and iPhone screens, which only differ in how they draw it.
@MainActor
@Observable
final class MusicBrowseModel {
    let library: Library
    private(set) var artists: [ItemSummary] = []
    private(set) var total = 0
    private(set) var recentAlbums: [ItemSummary] = []
    /// The profile's playlists (they span libraries, so every music library offers them).
    private(set) var playlists: [PlaylistSummary] = []
    private(set) var isLoading = false
    private(set) var error: String?
    private(set) var loadedAt: Date?
    /// Bumped by each reload, so a page that arrives for an older one is dropped.
    @ObservationIgnored private var generation = 0

    private static let pageSize = 60
    /// How many of the newest artists are opened to find the newest albums. The server lists albums only under their
    /// artist, so this bounds the requests a visit makes; an album newer than every one of these artists' can't exist.
    private static let recentArtists = 8
    private static let recentAlbumCount = 12

    init(library: Library) {
        self.library = library
    }

    var isEmpty: Bool { artists.isEmpty && !isLoading }

    func load(client: FlexarrClient) async {
        generation += 1
        let mine = generation
        isLoading = true
        defer { if mine == generation { isLoading = false } }
        do {
            async let newest = client.libraryItems(library.id, sort: .added, skip: 0, take: Self.recentArtists)
            async let lists = client.playlists()
            let page = try await client.libraryItems(library.id, sort: .title, skip: 0, take: Self.pageSize)
            guard mine == generation else { return }
            artists = page.items.filter { $0.kind == .artist || $0.kind == .unknown }
            total = page.total
            error = nil
            loadedAt = Date()
            playlists = (try? await lists) ?? []
            recentAlbums = await Self.newestAlbums(of: (try? await newest)?.items ?? [], client: client)
        } catch {
            if mine == generation { self.error = ConnectionError(error).message }
        }
    }

    func loadMoreIfNeeded(at index: Int, client: FlexarrClient) {
        guard !isLoading, artists.count < total, index >= artists.count - 18 else { return }
        let mine = generation
        let skip = artists.count
        isLoading = true
        Task {
            defer { if mine == generation { isLoading = false } }
            if let page = try? await client.libraryItems(library.id, sort: .title, skip: skip, take: Self.pageSize),
               mine == generation, skip == artists.count {
                let known = Set(artists.map(\.id))
                artists.append(contentsOf: page.items.filter { !known.contains($0.id) })
                total = page.total
            }
        }
    }

    /// The newest albums among these artists', newest first.
    private static func newestAlbums(of artists: [ItemSummary], client: FlexarrClient) async -> [ItemSummary] {
        let albums = await withTaskGroup(of: [ItemSummary].self) { group in
            for artist in artists {
                group.addTask { (try? await client.item(artist.id).children.filter { $0.kind == .album }) ?? [] }
            }
            var all: [ItemSummary] = []
            for await some in group { all += some }
            return all
        }
        return Array(albums.sorted { $0.addedAt > $1.addedAt }.prefix(recentAlbumCount))
    }
}

/// An artist's or an album's page: the detail (albums or tracks as `children`), loaded once and refreshed when it's
/// getting old, with the summary the viewer came from shown until it arrives.
@MainActor
@Observable
final class MusicPageModel {
    let id: String
    private(set) var detail: ItemDetail?
    private(set) var preview: ItemSummary?
    private(set) var error: String?
    private(set) var isLoading = false
    private(set) var loadedAt: Date?
    /// Tracks are being fetched for Play or Shuffle on an artist.
    private(set) var isQueueing = false

    init(id: String, preview: ItemSummary?) {
        self.id = id
        self.preview = preview
    }

    var item: ItemSummary? { detail?.item ?? preview }
    /// An artist's albums, newest first as the server lists them.
    var albums: [ItemSummary] { detail?.children.filter { $0.kind == .album } ?? [] }
    /// An album's tracks in play order.
    var tracks: [ItemSummary] { detail?.children.filter { $0.kind == .track } ?? [] }
    /// The artist an album belongs to.
    var artist: ItemSummary? { detail?.ancestors.last(where: { $0.kind == .artist }) }

    func load(client: FlexarrClient, onlyIfStale: Bool = false) async {
        if onlyIfStale, detail != nil, !Freshness.isStale(loadedAt) { return }
        isLoading = detail == nil
        defer { isLoading = false }
        do {
            let loaded = try await client.item(id)
            detail = loaded
            preview = loaded.item
            error = nil
            loadedAt = Date()
        } catch {
            if detail == nil { self.error = ConnectionError(error).message }
        }
    }

    /// Everything the page has, for Play and Shuffle: an album's own tracks, or an artist's whole catalogue (fetched
    /// when asked, not with the page, so a visit to an artist with a hundred albums stays light).
    func allTracks(client: FlexarrClient) async -> [ItemSummary] {
        if item?.kind == .album, !tracks.isEmpty { return tracks }
        isQueueing = true
        defer { isQueueing = false }
        return (try? await client.tracks(of: id)) ?? []
    }
}
