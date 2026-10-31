import FlexarrKit
import Foundation
import Network
import Observation
import Security
import UIKit

private let log = FlexarrLog("downloads")

/// A film, episode, track or podcast episode kept on this device for watching or listening with no connection.
struct LocalDownload: Codable, Identifiable, Hashable {
    enum State: Codable, Hashable {
        /// The server is converting it (0 to 1).
        case preparing(Double)
        /// Coming down to the device (0 to 1).
        case downloading(Double)
        case downloaded
        case failed(String)
    }

    /// The server's id for it.
    let id: String
    /// Whose it is: each profile sees only its own.
    let profileId: String
    var quality: DownloadQuality
    var item: ItemSummary
    var markers: [Marker]
    var durationSeconds: Double
    var nextId: String?
    var state: State
    var sizeBytes: Int64?
    /// In the downloads folder, once it's here.
    var fileName: String?
    /// What the server will send it as ("….flac", say), known once it's ready: the file is kept under that extension.
    var remoteFileName: String?
    /// Its picture: a film's own; tracks of one album share their cover.
    var imageFileName: String?
    /// Where watching got to, and when it was finished (an episode's deleted a day after, a podcast episode's at once).
    var position: Double = 0
    var finishedAt: Date?
    /// Watched offline and not yet told to the server.
    var unsent: UnsentProgress?
    /// Fetched by itself: as the next episode, or a podcast's newest (see `reconcilePodcasts`).
    var automatic = false
    /// Asked for by hand, for certain (set from this version on): only these may come over mobile data. Optional, so
    /// lists saved before it still read.
    var manual: Bool?
    var createdAt = Date()

    var isDownloaded: Bool { state == .downloaded && fileName != nil }
}

/// Read field by field, so a list saved by an older app (before `automatic` or `createdAt`, say) still reads: Swift's
/// own decoding ignores the defaults above and would fail the whole list. Only what it can't do without is required.
extension LocalDownload {
    private enum CodingKeys: String, CodingKey {
        case id, profileId, quality, item, markers, durationSeconds, nextId, state, sizeBytes, fileName, remoteFileName
        case imageFileName, position, finishedAt, unsent, automatic, manual, createdAt
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id)
        profileId = try c.decode(String.self, forKey: .profileId)
        item = try c.decode(ItemSummary.self, forKey: .item)
        state = try c.decode(State.self, forKey: .state)
        quality = try c.decodeIfPresent(DownloadQuality.self, forKey: .quality) ?? .original
        markers = (try? c.decodeIfPresent([Marker].self, forKey: .markers)) ?? []
        durationSeconds = try c.decodeIfPresent(Double.self, forKey: .durationSeconds) ?? 0
        nextId = try c.decodeIfPresent(String.self, forKey: .nextId)
        sizeBytes = try c.decodeIfPresent(Int64.self, forKey: .sizeBytes)
        fileName = try c.decodeIfPresent(String.self, forKey: .fileName)
        remoteFileName = try c.decodeIfPresent(String.self, forKey: .remoteFileName)
        imageFileName = try c.decodeIfPresent(String.self, forKey: .imageFileName)
        position = try c.decodeIfPresent(Double.self, forKey: .position) ?? 0
        finishedAt = try c.decodeIfPresent(Date.self, forKey: .finishedAt)
        unsent = try? c.decodeIfPresent(UnsentProgress.self, forKey: .unsent)
        automatic = try c.decodeIfPresent(Bool.self, forKey: .automatic) ?? false
        manual = try c.decodeIfPresent(Bool.self, forKey: .manual)
        createdAt = try c.decodeIfPresent(Date.self, forKey: .createdAt) ?? Date()
    }
}

struct UnsentProgress: Codable, Hashable {
    var position: Double
    var finished: Bool
    var playedAt: Date
}

/// An album or playlist downloaded whole: its tracks (as downloads of their own, in order) under one picture and
/// name, browsable and playable with no connection. A track in two collections is downloaded once.
struct LocalCollection: Codable, Identifiable, Hashable {
    enum Kind: String, Codable {
        case album, playlist
    }

    /// Where a collection's tracks stand, taken together.
    enum Status: Hashable {
        /// Tracks are still being asked for, or the server is getting them ready.
        case preparing
        /// Some are here (0 to 1 of the whole).
        case downloading(Double)
        case downloaded
        case failed(String)
    }

    /// The album's item id, or the playlist's id.
    let id: String
    let profileId: String
    let kind: Kind
    var title: String
    /// The artist, for an album.
    var subtitle: String?
    /// The album itself, for Now Playing's album name and the page's colour.
    var album: ItemSummary?
    var accentColor: String?
    var trackIds: [String]
    var imageFileName: String?
    /// About how big it'll be, from the server, before the files are here.
    var estimatedBytes: Int64?
    var createdAt = Date()

    /// What the tracks' states add up to: all here, some here, still preparing, or one that failed.
    static func status(of states: [LocalDownload.State?]) -> Status {
        guard !states.isEmpty else { return .preparing }
        if let failure = states.compactMap({ state -> String? in if case .failed(let message) = state { message } else { nil } }).first {
            return .failed(failure)
        }
        let fractions = states.map { state -> Double in
            switch state {
            case .downloaded: 1
            case .downloading(let fraction): fraction
            case .preparing, nil: 0
            case .failed: 0
            }
        }
        if fractions.allSatisfy({ $0 >= 1 }) { return .downloaded }
        if states.contains(where: { if case .downloading = $0 { true } else { $0 == .downloaded } }) {
            return .downloading(fractions.reduce(0, +) / Double(fractions.count))
        }
        return .preparing
    }
}

/// Read field by field, like `LocalDownload`, so a list saved by an older app still reads.
extension LocalCollection {
    private enum CodingKeys: String, CodingKey {
        case id, profileId, kind, title, subtitle, album, accentColor, trackIds, imageFileName, estimatedBytes, createdAt
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(String.self, forKey: .id)
        profileId = try c.decode(String.self, forKey: .profileId)
        kind = try c.decode(Kind.self, forKey: .kind)
        title = try c.decode(String.self, forKey: .title)
        trackIds = try c.decodeIfPresent([String].self, forKey: .trackIds) ?? []
        subtitle = try c.decodeIfPresent(String.self, forKey: .subtitle)
        album = try? c.decodeIfPresent(ItemSummary.self, forKey: .album)
        accentColor = try c.decodeIfPresent(String.self, forKey: .accentColor)
        imageFileName = try c.decodeIfPresent(String.self, forKey: .imageFileName)
        estimatedBytes = try c.decodeIfPresent(Int64.self, forKey: .estimatedBytes)
        createdAt = try c.decodeIfPresent(Date.self, forKey: .createdAt) ?? Date()
    }
}

/// Downloads on this device: asking the server to get them ready, fetching them in the background (even with the
/// app closed), playing them offline, sending what was watched once there's a connection, fetching the next
/// episode by itself (on Wi-Fi), keeping the newest episodes of podcasts that auto-download (on Wi-Fi), and deleting
/// episodes a day after they're finished and podcast episodes once they're played. Albums and playlists come as
/// collections of track downloads.
@MainActor
@Observable
final class DownloadStore: NSObject, OfflineLibrary, OfflineMusicLibrary {
    static let sessionIdentifier = "io.github.flexarr.downloads"

    private(set) var all: [LocalDownload] = []
    private(set) var collections: [LocalCollection] = []
    /// Collections whose tracks are still being asked of the server (observed, so a collection whose requests were
    /// cut short shows as failed as soon as they stop).
    private var queueing: Set<String> = []
    /// The profile watching: whose downloads show. Remembered, so they show with no connection too.
    private(set) var profileId: String? = UserDefaults.standard.string(forKey: "downloads.profile")
    /// The size downloads are made at: Medium unless changed in Settings.
    var quality: DownloadQuality = UserDefaults.standard.string(forKey: "downloads.quality").flatMap(DownloadQuality.init(rawValue:)) ?? .medium {
        didSet { UserDefaults.standard.set(quality.rawValue, forKey: "downloads.quality") }
    }
    /// Fetch the next episode by itself, on Wi-Fi.
    var downloadsNext: Bool = UserDefaults.standard.object(forKey: "downloads.next") as? Bool ?? true {
        didSet { UserDefaults.standard.set(downloadsNext, forKey: "downloads.next") }
    }
    /// Downloads the phone fetches by itself wait for Wi-Fi (on unless turned off in Settings). Off, they use mobile
    /// data too, though never in Low Data Mode.
    var autoDownloadsOnWiFiOnly: Bool = UserDefaults.standard.object(forKey: "downloads.wifiOnly") as? Bool ?? true {
        didSet {
            UserDefaults.standard.set(autoDownloadsOnWiFiOnly, forKey: "downloads.wifiOnly")
            if autoDownloadsOnWiFiOnly, !isOnWiFi { pauseWiFiOnlyTransfers() }
            Task { await catchUp() }
        }
    }
    private(set) var isOnline = true
    private(set) var isOnWiFi = false

    /// Whether a title is playing from its file now (it isn't deleted from under the player).
    @ObservationIgnored var isPlaying: (String) -> Bool = { _ in false }

    /// Called when the system has finished delivering background downloads (see the app delegate).
    @ObservationIgnored var backgroundCompletion: (() -> Void)?

    @ObservationIgnored private var client: FlexarrClient?
    @ObservationIgnored private var session: URLSession!
    /// A family guest's transfers: the system's background downloader runs apart from the app, so it can't reach the
    /// tunnel's port inside it. These carry on only while the app is open.
    @ObservationIgnored private lazy var guestSession: URLSession = {
        madeGuestSession = true
        return URLSession(configuration: .default, delegate: self, delegateQueue: .main)
    }()
    /// Whether `guestSession` exists: its transfers outlive guest mode (Leave cancels them after it's ended).
    @ObservationIgnored private var madeGuestSession = false
    /// Where a new transfer goes: the guest's session for a family guest, the background one for everyone else.
    private var transferSession: URLSession { GuestMode.isActive ? guestSession : session }

    /// Every transfer under way, in either session.
    private func allTasks(_ body: @escaping @Sendable ([URLSessionTask]) -> Void) {
        let sessions = madeGuestSession || GuestMode.isActive ? [session!, guestSession] : [session!]
        for session in sessions { session.getAllTasks(completionHandler: body) }
    }
    @ObservationIgnored private var polling: Task<Void, Never>?
    @ObservationIgnored private let monitor = NWPathMonitor()
    @ObservationIgnored private var lastSaved = Date.distantPast
    @ObservationIgnored private var isReconciling = false
    /// Podcast episodes whose download was deleted by hand (by episode id): never fetched by themselves again. Kept in
    /// the keychain too, which outlives reinstalling the app.
    @ObservationIgnored private var declined: Set<String> = DeclinedEpisodes.load() {
        didSet { DeclinedEpisodes.save(declined) }
    }
    /// Downloads deleted here that the server hasn't heard about yet (it was out of reach): told on the next catch-up,
    /// so it doesn't keep a copy, nor offer the episode again.
    @ObservationIgnored private var unsentDeletes: Set<String> = Set(UserDefaults.standard.stringArray(forKey: "downloads.unsentDeletes") ?? []) {
        didSet { UserDefaults.standard.set(Array(unsentDeletes), forKey: "downloads.unsentDeletes") }
    }
    /// Where transfers paused for want of Wi-Fi, or cut off part-way, had got to, to carry on from there.
    @ObservationIgnored private var resumeData: [String: Data] = [:]

    nonisolated static let folder: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        var folder = base.appending(path: "Downloads", directoryHint: .isDirectory)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        // Films aren't for iCloud backups: they're on the server already.
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? folder.setResourceValues(values)
        return folder
    }()

    private static let manifest = folder.appending(path: "downloads.json")
    private static let collectionsManifest = folder.appending(path: "collections.json")

    override init() {
        super.init()
        all = Self.loadSaved(LocalDownload.self, from: Self.manifest)
        collections = Self.loadSaved(LocalCollection.self, from: Self.collectionsManifest)
        let configuration = URLSessionConfiguration.background(withIdentifier: Self.sessionIdentifier)
        configuration.sessionSendsLaunchEvents = true
        configuration.isDiscretionary = false
        session = URLSession(configuration: configuration, delegate: self, delegateQueue: .main)
        monitor.pathUpdateHandler = { [weak self] path in
            let online = path.status == .satisfied
            let wifi = online && !path.isExpensive && !path.isConstrained
            Task { @MainActor in self?.connectivityChanged(online: online, wifi: wifi) }
        }
        monitor.start(queue: .main)
    }

    /// A saved list, keeping every entry that reads; a copy of the file is kept when any doesn't.
    private static func loadSaved<Element: Decodable>(_ type: Element.Type, from url: URL) -> [Element] {
        let loaded = SavedList.load(type, from: url)
        if loaded.unreadable > 0 {
            log.error("Couldn't read \(loaded.unreadable) saved \(url.lastPathComponent) entries; the file was kept as \(loaded.keptAt?.lastPathComponent ?? "nothing")")
        }
        return loaded.items
    }

    /// This profile's downloads, newest first.
    var downloads: [LocalDownload] {
        all.filter { $0.profileId == profileId }.sorted { $0.createdAt > $1.createdAt }
    }

    /// This profile's films and episodes (tracks show under their albums and playlists).
    var videoDownloads: [LocalDownload] { downloads.filter { $0.item.kind != .track && $0.item.kind != .podcastEpisode } }

    /// This profile's podcast episodes, newest first.
    var podcastDownloads: [LocalDownload] {
        downloads.filter { $0.item.kind == .podcastEpisode }.sorted { $0.item.addedAt > $1.item.addedAt }
    }

    /// This profile's albums and playlists, newest first.
    var musicCollections: [LocalCollection] {
        collections.filter { $0.profileId == profileId }.sorted { $0.createdAt > $1.createdAt }
    }

    func entry(for itemId: String) -> LocalDownload? {
        all.first { $0.profileId == profileId && $0.item.id == itemId }
    }

    func collection(for id: String) -> LocalCollection? {
        collections.first { $0.profileId == profileId && $0.id == id }
    }

    /// A collection's tracks in its order (those asked for so far).
    func tracks(in collection: LocalCollection) -> [LocalDownload] {
        collection.trackIds.compactMap(entry(for:))
    }

    func status(of collection: LocalCollection) -> LocalCollection.Status {
        let states = collection.trackIds.map { entry(for: $0)?.state }
        // A track never asked for (the requests were cut short) counts as failed, unless they're still being made.
        if !queueing.contains(collection.id), states.contains(where: { $0 == nil }) {
            return .failed("Some tracks weren't downloaded. Try again.")
        }
        return LocalCollection.status(of: states)
    }

    func isDownloaded(trackId: String) -> Bool {
        entry(for: trackId)?.isDownloaded == true
    }

    /// Every track of this album (or playlist) is here.
    func isDownloaded(collectionId: String) -> Bool {
        collection(for: collectionId).map { status(of: $0) == .downloaded } ?? false
    }

    var bytesUsed: Int64 {
        all.filter(\.isDownloaded).compactMap(\.sizeBytes).reduce(0, +)
    }

    func bytesUsed(by collection: LocalCollection) -> Int64 {
        tracks(in: collection).filter(\.isDownloaded).compactMap(\.sizeBytes).reduce(0, +)
    }

    // MARK: Connecting

    /// Signed in to a server (it can be asked for downloads).
    var isAttached: Bool { client != nil }

    /// The signed-in server and profile, as they change (nil when signed out or not known yet).
    func attach(client: FlexarrClient?, profileId: String?) {
        self.client = client
        if let profileId, profileId != self.profileId {
            self.profileId = profileId
            UserDefaults.standard.set(profileId, forKey: "downloads.profile")
        }
        Task { await catchUp() }
    }

    private func connectivityChanged(online: Bool, wifi: Bool) {
        let cameBack = online && !isOnline
        let wifiCameBack = wifi && !isOnWiFi
        isOnline = online
        isOnWiFi = wifi
        if !wifi { pauseWiFiOnlyTransfers() }
        if cameBack || wifiCameBack { Task { await catchUp() } }
    }

    // MARK: Wi-Fi only

    /// Fetched by itself, or a podcast episode not known for certain to have been asked for by hand: these never come
    /// over mobile data, an expensive network (a hotspot) or Low Data Mode.
    func isWiFiOnly(_ download: LocalDownload) -> Bool {
        autoDownloadsOnWiFiOnly && (download.automatic || (download.item.kind == .podcastEpisode && download.manual != true))
    }

    /// Held back until there's Wi-Fi.
    func isWaitingForWiFi(_ download: LocalDownload) -> Bool {
        guard isWiFiOnly(download), !isOnWiFi else { return false }
        switch download.state {
        case .preparing, .downloading: return true
        case .downloaded, .failed: return false
        }
    }

    /// Off Wi-Fi: transfers that mustn't use mobile data stop (where they'd got to is kept) and wait for Wi-Fi.
    private func pauseWiFiOnlyTransfers() {
        let held = Set(all.filter { isWiFiOnly($0) && !$0.isDownloaded }.map(\.id))
        guard !held.isEmpty else { return }
        allTasks { tasks in
            for task in tasks {
                guard let id = task.taskDescription, held.contains(id), let download = task as? URLSessionDownloadTask else { continue }
                download.cancel { data in
                    Task { @MainActor in self.held(id, resumeData: data) }
                }
            }
        }
    }

    /// A transfer stopped for want of Wi-Fi or cut off: back to waiting (the server still has it ready), to carry on
    /// later.
    private func held(_ id: String, resumeData data: Data?) {
        if let data { resumeData[id] = data }
        update(id) { if case .downloading = $0.state { $0.state = .preparing(1) } }
    }

    /// Sends what was watched offline, follows what the server is getting ready, deletes what's finished with, and
    /// fetches next episodes that are due.
    func catchUp() async {
        deleteFinished()
        guard let client, isOnline else { return }
        for id in unsentDeletes {
            do {
                try await client.deleteDownload(id)
                unsentDeletes.remove(id)
            } catch FlexarrClientError.http(status: 404, _) {
                unsentDeletes.remove(id)
            } catch {
                break // still out of reach
            }
        }
        for download in all where download.profileId == profileId {
            guard let unsent = download.unsent else { continue }
            do {
                try await client.reportOfflineProgress(itemId: download.item.id, positionSeconds: unsent.position, finished: unsent.finished, playedAt: unsent.playedAt)
                update(download.id) { if $0.unsent == unsent { $0.unsent = nil } }
            } catch {
                log.warning("Couldn't send offline progress", error: error)
            }
        }
        deleteFinished() // podcast episodes whose play was waiting to be sent
        await deletePlayedPodcasts()
        // A title saved by an app that didn't know its kind (a podcast episode, kept by an older version) was saved as
        // "unknown" and would stay so, filed under Films: what the server says now puts it right.
        for download in all where download.profileId == profileId && download.item.kind == .unknown {
            if let remote = try? await client.download(download.id), remote.item.kind != .unknown {
                update(download.id) { $0.item = remote.item }
            }
        }
        for download in downloads where download.isDownloaded && download.finishedAt == nil {
            fetchNext(after: download)
        }
        poll()
        await reconcilePodcasts()
    }

    // MARK: Podcasts

    /// Podcast episodes played since they were downloaded, wherever (on the web, say, or marked played): the server
    /// says which, and they go, unless one's playing here or has a play here still to send.
    private func deletePlayedPodcasts() async {
        guard let client, isOnline, downloads.contains(where: { $0.item.kind == .podcastEpisode }),
              let played = try? await client.podcastPlayedDownloads() else { return } // a server before 0.56, or out of reach
        let ids = Set(played.map(\.episodeId))
        for download in downloads where download.item.kind == .podcastEpisode && ids.contains(download.item.id) {
            if download.unsent == nil, !isPlaying(download.item.id) { delete(download.id) }
        }
    }

    /// Marked played here: a podcast episode's download goes at once, or a show's when all its episodes were.
    func markedPlayed(itemId: String) {
        for download in downloads where download.item.kind == .podcastEpisode && (download.item.id == itemId || download.item.parentId == itemId) {
            if !isPlaying(download.item.id) { delete(download.id) }
        }
    }

    /// Makes the phone hold what the server says it should: the newest unplayed episodes of each podcast that
    /// auto-downloads (none when the profile's Auto-download is off). Missing ones are fetched, on Wi-Fi; ones fetched
    /// by themselves that are no longer wanted (played, pushed out by newer ones) go, unless they're part-heard.
    func reconcilePodcasts() async {
        guard let client, isOnline, !isReconciling else { return }
        isReconciling = true
        defer { isReconciling = false }
        let wanted: [PodcastAutoDownload]
        do {
            wanted = try await client.podcastAutoDownloads()
        } catch {
            return // a server before podcasts, or none to be had
        }
        let wantedIds = Set(wanted.map(\.episodeId))
        for download in downloads where download.item.kind == .podcastEpisode && download.automatic && !wantedIds.contains(download.item.id) {
            if download.unsent == nil, download.finishedAt != nil || download.position == 0 { delete(download.id) }
        }
        guard isOnWiFi || (isOnline && !autoDownloadsOnWiFiOnly) else { return }
        for episode in wanted where entry(for: episode.episodeId) == nil && !declined.contains(episode.episodeId) {
            try? await download(itemId: episode.episodeId, quality: .original, automatic: true)
        }
    }

    /// Deletes a download by hand. A podcast episode's, however it came, is never fetched by itself again: the server
    /// is told (it then leaves the episode out of what phones should keep), and this phone remembers it too.
    func remove(_ id: String) {
        if let download = all.first(where: { $0.id == id }), download.item.kind == .podcastEpisode {
            declined.insert(download.item.id)
        }
        delete(id)
    }

    // MARK: Downloading

    /// Downloads a title at the size chosen in Settings. An original the phone can't play comes at High instead.
    func download(itemId: String) async throws {
        guard let client else { throw FlexarrClientError.invalidResponse }
        var quality = quality
        if quality == .original, let options = try? await client.downloadOptions(itemId),
           options.first(where: { $0.quality == .original })?.available == false {
            quality = .high
        }
        try await download(itemId: itemId, quality: quality)
    }

    /// Asks the server to get a title ready at this size, then fetches it when it is.
    func download(itemId: String, quality: DownloadQuality, automatic: Bool = false) async throws {
        guard let client, let profileId else { throw FlexarrClientError.invalidResponse }
        // Already here or on its way: nothing to do. Failed: start again.
        if let existing = entry(for: itemId) {
            guard case .failed = existing.state else { return }
            delete(existing.id)
        }
        let remote = try await client.requestDownload(itemId, quality: quality)
        var local = LocalDownload(id: remote.id, profileId: profileId, quality: quality, item: remote.item, markers: remote.markers,
                                  durationSeconds: remote.durationSeconds, nextId: remote.nextId, state: .preparing(remote.progress),
                                  sizeBytes: remote.sizeBytes, automatic: automatic, manual: automatic ? nil : true)
        all.removeAll { $0.id == remote.id }
        all.append(local)
        save()
        log.info("Downloading \(remote.item.title) at \(quality.rawValue)")
        if let image = await fetchImage(for: remote.item, id: remote.id) {
            local.imageFileName = image
            update(remote.id) { $0.imageFileName = image }
        }
        apply(remote)
        poll()
    }

    // MARK: Albums and playlists

    /// Downloads an album: a collection under its cover, and each track in order. Asking again for one that's here
    /// (or partly here) fetches what's missing.
    func download(album: ItemSummary, tracks: [ItemSummary]) async throws {
        guard let profileId else { throw FlexarrClientError.invalidResponse }
        var collection = self.collection(for: album.id)
            ?? LocalCollection(id: album.id, profileId: profileId, kind: .album, title: album.title, subtitle: album.artistName,
                               album: album, accentColor: album.accentColor, trackIds: [])
        collection.album = album
        collection.imageFileName = collection.imageFileName ?? Self.coverName(forAlbum: album.id)
        try await download(collection, tracks: tracks, coverPath: album.artwork.poster)
    }

    /// Downloads a playlist: its tracks as they are now, under its chosen cover (or its tracks' covers, as a mosaic).
    func download(playlist: PlaylistSummary, tracks: [ItemSummary]) async throws {
        guard let profileId else { throw FlexarrClientError.invalidResponse }
        var collection = self.collection(for: playlist.id)
            ?? LocalCollection(id: playlist.id, profileId: profileId, kind: .playlist, title: playlist.name, subtitle: nil,
                               album: nil, accentColor: tracks.first?.accentColor, trackIds: [])
        collection.title = playlist.name
        collection.imageFileName = collection.imageFileName ?? playlist.cover.map { _ in "playlist-\(playlist.id).jpg" }
        try await download(collection, tracks: tracks, coverPath: playlist.cover)
    }

    private func download(_ collection: LocalCollection, tracks: [ItemSummary], coverPath: String?) async throws {
        guard let client else { throw FlexarrClientError.invalidResponse }
        var collection = collection
        collection.trackIds = tracks.map(\.id)
        collections.removeAll { $0.id == collection.id && $0.profileId == collection.profileId }
        collections.append(collection)
        saveCollections()
        queueing.insert(collection.id)
        defer { queueing.remove(collection.id) }
        log.info("Downloading \(collection.title): \(tracks.count) tracks")

        if let name = collection.imageFileName, !FileManager.default.fileExists(atPath: Self.folder.appending(path: name).path(percentEncoded: false)) {
            await fetchImage(at: coverPath, named: name)
        }
        if collection.kind == .album, collection.estimatedBytes == nil,
           let estimate = try? await client.downloadOptions(collection.id).first?.estimatedBytes {
            updateCollection(collection.id) { $0.estimatedBytes = estimate }
        }
        for track in tracks {
            try await download(itemId: track.id, quality: .original)
        }
    }

    /// Deletes a collection and its tracks, except tracks another collection still holds.
    func remove(collectionId: String) {
        guard let collection = collection(for: collectionId) else { return }
        let kept = Set(collections.filter { $0.id != collection.id && $0.profileId == collection.profileId }.flatMap(\.trackIds))
        for trackId in collection.trackIds where !kept.contains(trackId) {
            if let track = entry(for: trackId) { delete(track.id) }
        }
        collections.removeAll { $0.id == collection.id && $0.profileId == collection.profileId }
        saveCollections()
        removeImageIfUnused(collection.imageFileName)
    }

    /// The album's cover, saved once for its tracks and the album itself.
    nonisolated private static func coverName(forAlbum albumId: String) -> String { "cover-\(albumId).jpg" }

    /// Follows the server getting downloads ready, every few seconds while any are.
    private func poll() {
        guard polling == nil else { return }
        polling = Task { [weak self] in
            while let self, !Task.isCancelled {
                // Ones waiting for Wi-Fi needn't be asked after until there is some.
                let waiting = all.filter {
                    guard case .preparing = $0.state else { return false }
                    return isOnWiFi || !isWiFiOnly($0)
                }
                guard !waiting.isEmpty, let client else { break }
                for download in waiting {
                    if let remote = try? await client.download(download.id) { apply(remote) }
                }
                try? await Task.sleep(for: .seconds(3))
            }
            self?.polling = nil
        }
    }

    /// What the server says about a download: still preparing, ready to fetch, or failed.
    private func apply(_ remote: OfflineDownload) {
        guard let local = all.first(where: { $0.id == remote.id }) else { return }
        switch remote.status {
        case .queued, .preparing:
            update(remote.id) { $0.state = .preparing(remote.progress) }
        case .ready:
            guard case .preparing = local.state, let client else { return }
            let wifiOnly = isWiFiOnly(local)
            update(remote.id) {
                $0.sizeBytes = remote.sizeBytes
                $0.nextId = remote.nextId
                $0.markers = remote.markers
                $0.remoteFileName = remote.fileName
            }
            // Ready, but this one waits for Wi-Fi: it starts when there is some (see connectivityChanged).
            if wifiOnly, !isOnWiFi {
                update(remote.id) { $0.state = .preparing(1) }
                return
            }
            update(remote.id) { $0.state = .downloading(0) }
            var request = client.downloadFileRequest(remote.id)
            if wifiOnly {
                // Never mobile data, a hotspot or Low Data Mode, should the network change mid-way: the system waits.
                request.allowsCellularAccess = false
                request.allowsExpensiveNetworkAccess = false
                request.allowsConstrainedNetworkAccess = false
            } else if local.automatic {
                // Allowed on mobile data (Settings), but not in Low Data Mode.
                request.allowsConstrainedNetworkAccess = false
            }
            let task = resumeData.removeValue(forKey: remote.id).map { transferSession.downloadTask(withResumeData: $0) } ?? transferSession.downloadTask(with: request)
            task.taskDescription = remote.id
            task.resume()
        case .removed where !local.isDownloaded:
            // Deleted on another of this profile's phones: nothing to fetch.
            all.removeAll { $0.id == remote.id }
            save()
        case .received where !local.isDownloaded, .expired:
            update(remote.id) { $0.state = .failed("This download ran out on the server. Download it again.") }
        case .failed:
            update(remote.id) { $0.state = .failed(remote.error ?? "The server couldn't get this ready.") }
        default:
            break
        }
    }

    /// Saves the picture a download shows: a film's backdrop, an episode's still, or a track's album cover (one file
    /// for the whole album, kept as long as anything uses it).
    private func fetchImage(for item: ItemSummary, id: String) async -> String? {
        if item.kind == .podcastEpisode {
            return await fetchImage(at: item.artwork.thumb ?? item.artwork.poster, named: "\(id).jpg", width: 600)
        }
        if item.kind == .track {
            let name = Self.coverName(forAlbum: item.parentId ?? item.id)
            if FileManager.default.fileExists(atPath: Self.folder.appending(path: name).path(percentEncoded: false)) { return name }
            return await fetchImage(at: item.artwork.poster, named: name, width: 600)
        }
        let path = item.kind == .episode ? (item.artwork.thumb ?? item.artwork.backdrop) : (item.artwork.backdrop ?? item.artwork.poster)
        return await fetchImage(at: path, named: "\(id).jpg")
    }

    @discardableResult
    private func fetchImage(at path: String?, named name: String, width: Int = 500) async -> String? {
        guard let url = client?.imageURL(path, width: width), let (data, _) = try? await URLSession.shared.data(from: url), UIImage(data: data) != nil else {
            return nil
        }
        try? data.write(to: Self.folder.appending(path: name))
        return name
    }

    // MARK: Deleting

    func delete(_ id: String) {
        guard let download = all.first(where: { $0.id == id }) else { return }
        allTasks { tasks in
            for task in tasks where task.taskDescription == id { task.cancel() }
        }
        if let name = download.fileName {
            try? FileManager.default.removeItem(at: Self.folder.appending(path: name))
        }
        all.removeAll { $0.id == id }
        save()
        removeImageIfUnused(download.imageFileName)
        resumeData[id] = nil
        // The server drops its copy (and, for a podcast episode, won't offer it again); out of reach, it's told later.
        unsentDeletes.insert(id)
        if let client {
            Task {
                do {
                    try await client.deleteDownload(id)
                    unsentDeletes.remove(id)
                } catch FlexarrClientError.http(status: 404, _) {
                    unsentDeletes.remove(id)
                } catch {}
            }
        }
    }

    /// A picture goes once nothing shows it any more (an album's cover is shared by its tracks).
    private func removeImageIfUnused(_ name: String?) {
        guard let name, !all.contains(where: { $0.imageFileName == name }), !collections.contains(where: { $0.imageFileName == name }) else { return }
        try? FileManager.default.removeItem(at: Self.folder.appending(path: name))
    }

    /// Signing out: every download goes, whoever's it was (the server keeps its record of what was declined).
    /// Watched offline and not yet sent, kept from downloads deleted by signing out until the sign-in has sent it.
    private var leftoverProgress: [(itemId: String, progress: UnsentProgress)] = []

    /// Signing out: sends what was watched offline and not yet sent, with the sign-in that's still working, whether
    /// or not the downloads have been deleted yet.
    func sendLeftoverProgress(with client: FlexarrClient) async {
        let pending = leftoverProgress + all.compactMap { d in d.unsent.map { (d.item.id, $0) } }
        leftoverProgress = []
        for (itemId, unsent) in pending {
            try? await client.reportOfflineProgress(itemId: itemId, positionSeconds: unsent.position, finished: unsent.finished, playedAt: unsent.playedAt)
        }
    }

    func deleteAll() {
        leftoverProgress += all.compactMap { d in d.unsent.map { (d.item.id, $0) } }
        for id in all.map(\.id) { delete(id) }
        let pictures = collections.compactMap(\.imageFileName)
        collections = []
        saveCollections()
        for name in pictures { removeImageIfUnused(name) }
        profileId = nil
        UserDefaults.standard.removeObject(forKey: "downloads.profile")
    }

    /// Podcast episodes played to the end, once the server's been told (and the player's moved on), and episodes
    /// finished more than a day ago (films are kept until deleted).
    private func deleteFinished() {
        let dayAgo = Date().addingTimeInterval(-24 * 60 * 60)
        for download in all where download.unsent == nil {
            guard let finished = download.finishedAt else { continue }
            switch download.item.kind {
            case .podcastEpisode where !isPlaying(download.item.id): delete(download.id)
            case .episode where finished < dayAgo: delete(download.id)
            default: break
            }
        }
    }

    // MARK: Next episode

    /// Keeps one episode ahead: when one's downloaded or watched, fetch the next, on Wi-Fi, at the same size.
    private func fetchNext(after download: LocalDownload) {
        guard downloadsNext, isOnWiFi || !autoDownloadsOnWiFiOnly, download.item.kind == .episode, let nextId = download.nextId, entry(for: nextId) == nil else { return }
        Task { try? await self.download(itemId: nextId, quality: download.quality, automatic: true) }
    }

    // MARK: Playing (OfflineLibrary)

    /// How to play a download: from where it got to, with the next episode if that's downloaded too.
    func playbackInfo(for download: LocalDownload, preferences: PlaybackPreferences?) -> PlaybackInfo? {
        guard let name = download.fileName else { return nil }
        let next = download.nextId.flatMap(entry(for:)).flatMap { $0.isDownloaded ? $0.item : nil }
        let start = download.finishedAt == nil && download.position > 30 ? download.position : 0
        return .offline(file: Self.folder.appending(path: name), item: download.item, markers: download.markers,
                        durationSeconds: download.durationSeconds, startPositionSeconds: start, next: next, preferences: preferences)
    }

    func record(itemId: String, position: Double, finished: Bool) {
        guard let download = entry(for: itemId) else { return }
        update(download.id) {
            $0.position = finished ? 0 : position
            if finished { $0.finishedAt = $0.finishedAt ?? Date() } else if position > 30 { $0.finishedAt = nil }
            $0.unsent = UnsentProgress(position: position, finished: finished, playedAt: Date())
        }
        if finished, let updated = entry(for: itemId) { fetchNext(after: updated) }
        Task { await catchUp() }
    }

    func forgetPosition(itemId: String) {
        guard let download = entry(for: itemId), download.finishedAt == nil else { return }
        update(download.id) {
            $0.position = 0
            $0.unsent = nil
        }
    }

    func nextInfo(after info: PlaybackInfo) -> PlaybackInfo? {
        guard let next = info.next, let download = entry(for: next.id), download.isDownloaded else { return nil }
        return playbackInfo(for: download, preferences: info.preferences)
    }

    // MARK: Listening (OfflineMusicLibrary)

    func localFile(for trackId: String) -> URL? {
        guard let download = entry(for: trackId), download.isDownloaded, let name = download.fileName else { return nil }
        return Self.folder.appending(path: name)
    }

    func resumePosition(for trackId: String) -> Double {
        guard let download = entry(for: trackId), download.finishedAt == nil else { return 0 }
        return download.position
    }

    func nextDownloadedEpisode(after episode: ItemSummary) -> ItemSummary? {
        podcastDownloads
            .filter { $0.isDownloaded && $0.finishedAt == nil && $0.item.id != episode.id && $0.item.seriesId == episode.seriesId && $0.item.addedAt > episode.addedAt }
            .min { $0.item.addedAt < $1.item.addedAt }?.item
    }

    func localArtwork(for trackId: String) -> UIImage? {
        image(named: entry(for: trackId)?.imageFileName)
    }

    /// A saved picture, by the name a download or collection holds.
    func image(named name: String?) -> UIImage? {
        guard let name else { return nil }
        return UIImage(contentsOfFile: Self.folder.appending(path: name).path(percentEncoded: false))
    }

    // MARK: Saving

    private func update(_ id: String, _ change: (inout LocalDownload) -> Void) {
        guard let index = all.firstIndex(where: { $0.id == id }) else { return }
        change(&all[index])
        save()
    }

    private func updateCollection(_ id: String, _ change: (inout LocalCollection) -> Void) {
        guard let index = collections.firstIndex(where: { $0.id == id && $0.profileId == profileId }) else { return }
        change(&collections[index])
        saveCollections()
    }

    private static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }()

    private func save(throttled: Bool = false) {
        if throttled, Date().timeIntervalSince(lastSaved) < 2 { return }
        lastSaved = Date()
        if let data = try? Self.encoder.encode(all) { try? data.write(to: Self.manifest, options: .atomic) }
    }

    private func saveCollections() {
        if let data = try? Self.encoder.encode(collections) { try? data.write(to: Self.collectionsManifest, options: .atomic) }
    }

    fileprivate func progressed(_ id: String, _ fraction: Double) {
        guard let index = all.firstIndex(where: { $0.id == id }) else { return }
        all[index].state = .downloading(fraction)
        save(throttled: true)
    }

    fileprivate func arrived(_ id: String, fileName: String, bytes: Int64) {
        update(id) {
            $0.state = .downloaded
            $0.fileName = fileName
            $0.sizeBytes = bytes
        }
        log.info("Downloaded \(id)")
        if let client { Task { try? await client.markDownloadReceived(id) } }
        if let download = all.first(where: { $0.id == id }) { fetchNext(after: download) }
    }

    fileprivate func stopped(_ id: String, _ error: Error) {
        guard let download = all.first(where: { $0.id == id }), !download.isDownloaded else { return }
        if (error as? URLError)?.code == .cancelled { return }
        // Cut off part-way (the network went, or a guest's tunnel slept with the phone locked): back to waiting, to
        // carry on from where it got to once the server's in reach again (the next catch-up, back in front).
        if let data = (error as NSError).userInfo[NSURLSessionDownloadTaskResumeData] as? Data {
            held(id, resumeData: data)
            if isOnline { poll() }
            return
        }
        update(id) { $0.state = .failed("The download stopped. Try again.") }
    }
}

extension DownloadStore: URLSessionDownloadDelegate {
    nonisolated func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                                totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        guard let id = downloadTask.taskDescription, totalBytesExpectedToWrite > 0 else { return }
        let fraction = Double(totalBytesWritten) / Double(totalBytesExpectedToWrite)
        MainActor.assumeIsolated { progressed(id, fraction) }
    }

    /// The file must be moved before this returns: the system deletes it afterwards.
    nonisolated func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        guard let id = downloadTask.taskDescription else { return }
        let status = (downloadTask.response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 || status == 206 else {
            MainActor.assumeIsolated { stopped(id, URLError(.badServerResponse)) }
            return
        }
        // Kept under the extension the server sent it as (a track's .flac or .mp3), so the player opens it right.
        let sent = downloadTask.response?.suggestedFilename.map { ($0 as NSString).pathExtension } ?? ""
        let known = MainActor.assumeIsolated { all.first { $0.id == id }?.remoteFileName.map { ($0 as NSString).pathExtension } ?? "" }
        let name = "\(id).\([sent, known, "mp4"].first { !$0.isEmpty && $0.count <= 5 } ?? "mp4")"
        let destination = DownloadStore.folder.appending(path: name)
        try? FileManager.default.removeItem(at: destination)
        do {
            try FileManager.default.moveItem(at: location, to: destination)
            let bytes = (try? destination.resourceValues(forKeys: [.fileSizeKey]).fileSize).map(Int64.init) ?? 0
            MainActor.assumeIsolated { arrived(id, fileName: name, bytes: bytes) }
        } catch {
            MainActor.assumeIsolated { stopped(id, error) }
        }
    }

    nonisolated func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: (any Error)?) {
        guard let error, let id = task.taskDescription else { return }
        MainActor.assumeIsolated { stopped(id, error) }
    }

    nonisolated func urlSessionDidFinishEvents(forBackgroundURLSession session: URLSession) {
        MainActor.assumeIsolated {
            backgroundCompletion?()
            backgroundCompletion = nil
        }
    }
}

/// The podcast episodes this phone was told not to keep (their downloads deleted by hand), by episode id: in the app's
/// settings and the keychain, which outlives reinstalling the app.
private enum DeclinedEpisodes {
    private static let key = "downloads.podcasts.declined"
    private static var query: [String: Any] { [
        kSecClass as String: kSecClassGenericPassword,
        kSecAttrService as String: "io.github.flexarr.declined-episodes",
        kSecAttrAccount as String: "episodes",
    ] }

    static func load() -> Set<String> {
        var ids = Set(UserDefaults.standard.stringArray(forKey: key) ?? [])
        var lookup = query
        lookup[kSecReturnData as String] = true
        var result: CFTypeRef?
        if SecItemCopyMatching(lookup as CFDictionary, &result) == errSecSuccess, let data = result as? Data,
           let saved = try? JSONDecoder().decode([String].self, from: data) {
            ids.formUnion(saved)
        }
        return ids
    }

    static func save(_ ids: Set<String>) {
        let list = Array(ids)
        UserDefaults.standard.set(list, forKey: key)
        guard let data = try? JSONEncoder().encode(list) else { return }
        if SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary) == errSecItemNotFound {
            var item = query
            item[kSecValueData as String] = data
            item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
            SecItemAdd(item as CFDictionary, nil)
        }
    }
}
