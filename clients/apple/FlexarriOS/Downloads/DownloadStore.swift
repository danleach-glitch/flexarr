import FlexarrKit
import Foundation
import Network
import Observation
import Security
#if canImport(UIKit)
import UIKit
#else
import AppKit
#endif

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
    /// Whether `position` was reached on this phone (true) or taken from the server (false: where another device had
    /// got to, when it was asked for or since). Nil in lists saved before it was kept.
    var placeHeardHere: Bool?
    /// Watched offline and not yet told to the server.
    var unsent: UnsentProgress?
    /// Fetched by itself: as the next episode, or a podcast's newest (see `reconcilePodcasts`).
    var automatic = false
    /// Asked for by hand, for certain (set from this version on): only these may come over mobile data. Optional, so
    /// lists saved before it still read.
    var manual: Bool?
    var createdAt = Date()
    /// A book: its files, fetched one by one under this one entry (whose `fileName` is their folder).
    var book: LocalBook?
    /// Other titles this was asked for as: a linked copy's (a 1080p episode's, say), whose download the server found
    /// already made through this one. It's found under those ids too, so the copy counts as here.
    var askedAs: [String]?

    /// Its own id, or one it was asked for as (see `askedAs`).
    func isDownload(of itemId: String) -> Bool { item.id == itemId || askedAs?.contains(itemId) == true }

    var isDownloaded: Bool { state == .downloaded && fileName != nil }

    var hasFailed: Bool { if case .failed = state { true } else { false } }
}

/// A downloaded book: the manifest it was fetched with (the item's own file addresses, its chapters and timeline) and
/// each file's name in its folder once it's here.
struct LocalBook: Codable, Hashable {
    var manifest: BookManifest
    var files: [String?]
    /// A speed chosen while it played from here, not yet sent to the server.
    var unsentSpeed: Double?

    var isComplete: Bool { !files.isEmpty && files.allSatisfy { $0 != nil } }
}

/// Read field by field, so a list saved by an older app (before `automatic` or `createdAt`, say) still reads: Swift's
/// own decoding ignores the defaults above and would fail the whole list. Only what it can't do without is required.
extension LocalDownload {
    private enum CodingKeys: String, CodingKey {
        case id, profileId, quality, item, markers, durationSeconds, nextId, state, sizeBytes, fileName, remoteFileName
        case imageFileName, position, finishedAt, placeHeardHere, unsent, automatic, manual, createdAt, book, askedAs
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
        placeHeardHere = try? c.decodeIfPresent(Bool.self, forKey: .placeHeardHere)
        unsent = try? c.decodeIfPresent(UnsentProgress.self, forKey: .unsent)
        automatic = try c.decodeIfPresent(Bool.self, forKey: .automatic) ?? false
        manual = try c.decodeIfPresent(Bool.self, forKey: .manual)
        createdAt = try c.decodeIfPresent(Date.self, forKey: .createdAt) ?? Date()
        book = try? c.decodeIfPresent(LocalBook.self, forKey: .book)
        askedAs = try? c.decodeIfPresent([String].self, forKey: .askedAs)
    }
}

struct UnsentProgress: Codable, Hashable {
    var position: Double
    var finished: Bool
    var playedAt: Date
    /// How many times the server has said it has no such title (see `UnsentReports.notFoundAgain`). Nil in lists
    /// saved before it was counted.
    var notFound: Int?
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

    private(set) var all: [LocalDownload] = [] {
        didSet {
            refreshDownloadedIds()
            keepAwakeForAwayDownloads()
        }
    }

    /// Away from home the downloads come through the tunnel, which stops once the app is suspended (the screen
    /// locking by itself, say): the screen stays on while any is under way, so they finish.
    private func keepAwakeForAwayDownloads() {
        #if canImport(UIKit)
        KeepAwake.set(.awayDownloads, GuestMode.isActive && all.contains { download in
            switch download.state {
            case .preparing, .downloading: !isWaitingForWiFi(download)
            case .downloaded, .failed: false
            }
        })
        #endif
    }
    private(set) var collections: [LocalCollection] = []
    /// The ids of this profile's downloads that are here to play, kept apart from `all` so what only asks "is this
    /// downloaded?" (a card's mark) isn't drawn again on every step of a transfer's progress: it changes only when a
    /// download arrives, goes, or the profile changes.
    private(set) var downloadedIds: Set<String> = []
    /// Episodes whose season's or show's download was stopped: a batch still asking for them gives up (GroupDownloads).
    var stoppedEpisodeIds: Set<String> = []
    /// Shows and seasons whose whole download was started here ("profile:id"), so only their own control shows its
    /// progress: one episode downloading doesn't make its season or show look started (GroupDownloads).
    var startedGroups: Set<String> = Set(UserDefaults.standard.stringArray(forKey: "downloads.groups") ?? []) {
        didSet { UserDefaults.standard.set(Array(startedGroups), forKey: "downloads.groups") }
    }
    /// Collections whose tracks are still being asked of the server (observed, so a collection whose requests were
    /// cut short shows as failed as soon as they stop).
    private var queueing: Set<String> = []
    /// The profile watching: whose downloads show. Remembered, so they show with no connection too.
    private(set) var profileId: String? = UserDefaults.standard.string(forKey: "downloads.profile") {
        didSet { refreshDownloadedIds() }
    }
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
            if !isOnWiFi {
                // Off Wi-Fi now: on, transfers that mustn't use mobile data stop; off, ones made to wait for Wi-Fi start
                // again under the new rule (books' files at the catch-up).
                if autoDownloadsOnWiFiOnly { pauseWiFiOnlyTransfers() } else { restartTransfersWaitingForWiFi() }
            }
            Task { await catchUp() }
        }
    }
    /// Fetch the next book in a series by itself once one's finished (as automatic downloads do).
    var downloadsNextBook: Bool = UserDefaults.standard.object(forKey: "downloads.nextBook") as? Bool ?? false {
        didSet { UserDefaults.standard.set(downloadsNextBook, forKey: "downloads.nextBook") }
    }
    /// A book's download goes once it's finished.
    var removesFinishedBooks: Bool = UserDefaults.standard.object(forKey: "downloads.removeFinishedBooks") as? Bool ?? false {
        didSet { UserDefaults.standard.set(removesFinishedBooks, forKey: "downloads.removeFinishedBooks") }
    }
    private(set) var isOnline = true
    /// How far each of a book's files has come down (download id, then file), for the whole book's progress.
    @ObservationIgnored private var bookFileProgress: [String: [Int: Double]] = [:]
    /// When each transfer's progress was last shown, so it's shown only a few times a second.
    @ObservationIgnored private var progressShownAt: [String: Date] = [:]
    /// Books whose next in the series is being looked up, so it's fetched once.
    @ObservationIgnored private var fetchingNextBook: Set<String> = []
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
    /// When each download being got ready was last asked after (see `poll`).
    @ObservationIgnored private var lastAsked: [String: Date] = [:]
    @ObservationIgnored private let monitor = NWPathMonitor()
    @ObservationIgnored private var lastSaved = Date.distantPast
    /// Writes `all` to its file away from the main thread, at most every couple of seconds (see `save`).
    @ObservationIgnored private let manifestWriter = SavedList.Writer<LocalDownload>(to: DownloadStore.manifest, encoder: DownloadStore.makeEncoder())
    @ObservationIgnored private var isReconciling = false
    /// Podcast episodes whose download was deleted by hand (by episode id): never fetched by themselves again. Kept in
    /// the keychain too, which outlives reinstalling the app.
    @ObservationIgnored private var declined: Set<String> = DeclinedEpisodes.load() {
        didSet { DeclinedEpisodes.save(declined) }
    }
    /// Episodes whose download was deleted by hand (by episode id): never fetched by themselves as the next episode.
    @ObservationIgnored var skippedNext: Set<String> = Set(UserDefaults.standard.stringArray(forKey: "downloads.skippedNext") ?? []) {
        didSet { UserDefaults.standard.set(Array(skippedNext), forKey: "downloads.skippedNext") }
    }
    /// Downloads deleted here that the server hasn't heard about yet (`ProfileTells` entries): out of reach, or another
    /// profile's, told on a catch-up signed in as theirs, so it doesn't keep a copy, nor offer the episode again.
    @ObservationIgnored private var unsentDeletes: Set<String> = Set(UserDefaults.standard.stringArray(forKey: "downloads.unsentDeletes") ?? []) {
        didSet { UserDefaults.standard.set(Array(unsentDeletes), forKey: "downloads.unsentDeletes") }
    }
    /// Those of `unsentDeletes` the app took off by itself (keeping only the newest few, once played, signing out, trying
    /// again), not someone by hand: the server is told so, and doesn't leave such a podcast episode out from then on.
    /// Any not here (made before this was kept) count as by hand, as they always did.
    @ObservationIgnored private var unsentAutomaticDeletes: Set<String> = Set(UserDefaults.standard.stringArray(forKey: "downloads.unsentAutomaticDeletes") ?? []) {
        didSet { UserDefaults.standard.set(Array(unsentAutomaticDeletes), forKey: "downloads.unsentAutomaticDeletes") }
    }
    /// Downloads that have come whose server hasn't been told yet (`ProfileTells` entries): out of reach, or another
    /// profile's, told on a catch-up signed in as theirs. Untold, the server keeps its copy of the file for a week.
    @ObservationIgnored private var unsentReceipts: Set<String> = Set(UserDefaults.standard.stringArray(forKey: "downloads.unsentReceipts") ?? []) {
        didSet { UserDefaults.standard.set(Array(unsentReceipts), forKey: "downloads.unsentReceipts") }
    }
    /// Where transfers paused for want of Wi-Fi, or cut off part-way, had got to, to carry on from there.
    @ObservationIgnored private var resumeData: [String: Resumable] = [:]
    /// Transfers waiting on their address being checked before they're made (see `startTransfer`).
    @ObservationIgnored private var startingTransfers: Set<String> = []

    /// Where a transfer had got to, whether it was made Wi-Fi only, and the address it was going to: resume data keeps
    /// its request (network rule, address and sign-in), so it's only used while that rule holds and at that same
    /// address once it has said who it is (see `startTransfer`).
    private struct Resumable {
        let data: Data
        let wifiOnly: Bool
        let url: URL?
    }

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
        let saved = SavedList.load(LocalDownload.self, from: Self.manifest)
        let savedCollections = SavedList.load(LocalCollection.self, from: Self.collectionsManifest)
        Self.report(saved, from: Self.manifest)
        Self.report(savedCollections, from: Self.collectionsManifest)
        all = saved.items
        collections = savedCollections.items
        refreshDownloadedIds()
        // Only when every entry read: an unreadable one's files are still its own.
        if saved.unreadable == 0, savedCollections.unreadable == 0 { sweepUnknownFiles() }
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
        // Leaving the front (or closing): whatever's waiting to be saved is written first, since the app can be
        // suspended at any moment after. Once more on reaching the background: something saved as the app was leaving
        // the front (where a film watched offline had got to, say), after the first of these had run, would otherwise
        // still be waiting its turn when the app is suspended, and lost if it's then closed to make room.
        #if canImport(UIKit)
        let leaving = [UIApplication.willResignActiveNotification, UIApplication.didEnterBackgroundNotification, UIApplication.willTerminateNotification]
        #else
        let leaving = [NSApplication.didResignActiveNotification, NSApplication.willTerminateNotification]
        #endif
        for name in leaving {
            NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [manifestWriter] _ in manifestWriter.flush() }
        }
    }

    /// A saved list's entries that didn't read (each was kept, with a copy of the file).
    private static func report<Element>(_ loaded: SavedList.Loaded<Element>, from url: URL) {
        if loaded.unreadable > 0 {
            log.error("Couldn't read \(loaded.unreadable) saved \(url.lastPathComponent) entries; the file was kept as \(loaded.keptAt?.lastPathComponent ?? "nothing")")
        }
    }

    /// Files in the downloads folder that no download or collection holds (one that finished coming down as it was
    /// deleted, a picture saved just before the app was closed): they'd only take space, out of sight. The saved
    /// lists, and copies of ones that didn't read, stay.
    private func sweepUnknownFiles() {
        let known = DownloadSweep.known(fileNames: all.map(\.fileName) + all.map(\.imageFileName) + collections.map(\.imageFileName))
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: Self.folder.path(percentEncoded: false)) else { return }
        for name in DownloadSweep.unknown(names, known: known) {
            log.info("Removing a file no download holds: \(name)")
            try? FileManager.default.removeItem(at: Self.folder.appending(path: name))
        }
    }

    /// This profile's downloads, newest first.
    var downloads: [LocalDownload] {
        all.filter { $0.profileId == profileId }.sorted { $0.createdAt > $1.createdAt }
    }

    /// This profile's films and episodes (tracks show under their albums and playlists; kinds this app doesn't know aren't videos).
    var videoDownloads: [LocalDownload] { downloads.filter { [.movie, .episode].contains($0.item.kind) } }

    /// This profile's books, newest first.
    var bookDownloads: [LocalDownload] { downloads.filter { $0.item.kind == .audiobook } }

    /// This profile's podcast episodes, newest first.
    var podcastDownloads: [LocalDownload] {
        downloads.filter { $0.item.kind == .podcastEpisode }.sorted { $0.item.addedAt > $1.item.addedAt }
    }

    /// This profile's albums and playlists, newest first.
    var musicCollections: [LocalCollection] {
        collections.filter { $0.profileId == profileId }.sorted { $0.createdAt > $1.createdAt }
    }

    /// This profile's download of a title: its own, or else one the server found already made through a linked copy
    /// (`askedAs`).
    func entry(for itemId: String) -> LocalDownload? {
        all.first { $0.profileId == profileId && $0.item.id == itemId }
            ?? all.first { $0.profileId == profileId && $0.askedAs?.contains(itemId) == true }
    }

    /// A title's download, made through whichever of its linked copies.
    func entry(for itemId: String, orCopies copies: [String]) -> LocalDownload? {
        entry(for: itemId) ?? copies.lazy.compactMap(entry(for:)).first
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
            return .failed(t("Some tracks weren’t downloaded. Try again."))
        }
        return LocalCollection.status(of: states)
    }

    /// A film, episode, track, book or podcast episode of this profile's is here to play (what
    /// `entry(for:)?.isDownloaded` says, without following its progress).
    func isDownloaded(trackId: String) -> Bool {
        downloadedIds.contains(trackId)
    }

    /// Works out `downloadedIds` again, setting it only when it changed (setting it at all redraws what reads it).
    private func refreshDownloadedIds() {
        var seen = Set<String>(), here = Set<String>()
        for download in all where download.profileId == profileId {
            if seen.insert(download.item.id).inserted, download.isDownloaded { here.insert(download.item.id) }
            if download.isDownloaded, let others = download.askedAs { here.formUnion(others) }
        }
        if here != downloadedIds { downloadedIds = here }
    }

    /// Every track of this album (or playlist) is here.
    func isDownloaded(collectionId: String) -> Bool {
        guard let collection = collection(for: collectionId), !collection.trackIds.isEmpty else { return false }
        return collection.trackIds.allSatisfy(downloadedIds.contains)
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

    /// The profile the attached sign-in is known to act as (nil until it's known): the server's check of what may
    /// still be played is asked only for that profile's downloads.
    @ObservationIgnored private var attachedProfileId: String?

    /// The signed-in server and profile, as they change (nil when signed out or not known yet).
    func attach(client: FlexarrClient?, profileId: String?) {
        self.client = client
        attachedProfileId = client == nil ? nil : profileId
        if let client {
            // A family guest's address is the tunnel's loopback one, the same whoever's Flexarr it joins: its plays are
            // never kept past its downloads, so none could reach another host's history. A household device with a home
            // address is known by that, at home or through the tunnel, so a download made at home matches away.
            let tunnelOnly = client.addresses.guest != nil && !client.addresses.hasHomeRoute
            let server = tunnelOnly ? nil : UnsentReports.serverKey(client.addresses.home)
            if server != self.server {
                self.server = server
                UserDefaults.standard.set(server, forKey: "downloads.server")
            }
        }
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
        if !wifi {
            pauseWiFiOnlyTransfers()
            // Ones made Wi-Fi only before that was turned off would wait for Wi-Fi: started again with mobile data.
            restartTransfersWaitingForWiFi()
            resumeBooks()
        }
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
                guard let id = task.taskDescription, held.contains(id) || held.contains(String(id.split(separator: "#").first ?? "")), let download = task as? URLSessionDownloadTask else { continue }
                let wifiOnly = Self.madeWiFiOnly(download)
                let url = Self.address(of: download)
                download.cancel { data in
                    Task { @MainActor in self.held(id, resumeData: data, wifiOnly: wifiOnly, url: url) }
                }
            }
        }
    }

    /// Off Wi-Fi: films', episodes' and tracks' transfers made Wi-Fi only that no longer need be (the setting was
    /// turned off since) would wait for Wi-Fi for ever, as a transfer's network rule can't change. Cancelled and back
    /// to waiting, so the next look at the server starts them afresh with mobile data. Books' files: `startBookFiles`.
    private func restartTransfersWaitingForWiFi() {
        allTasks { tasks in
            let stranded = tasks.filter { $0.state == .running || $0.state == .suspended }.filter(Self.madeWiFiOnly)
                .compactMap { task in task.taskDescription.map { (task, $0) } }
                .filter { Self.bookFile($0.1) == nil }
            guard !stranded.isEmpty else { return }
            Task { @MainActor in
                for (task, id) in stranded {
                    guard let download = self.all.first(where: { $0.id == id }), !download.isDownloaded, !self.isWiFiOnly(download) else { continue }
                    task.cancel()
                    self.resumeData[id] = nil
                    self.held(id, resumeData: nil, wifiOnly: false)
                }
                if self.isOnline { self.poll() }
            }
        }
    }

    /// Whether a transfer was made never to use mobile data.
    nonisolated private static func madeWiFiOnly(_ task: URLSessionTask) -> Bool {
        (task.originalRequest ?? task.currentRequest)?.allowsCellularAccess == false
    }

    /// The address a transfer was made for.
    nonisolated private static func address(of task: URLSessionTask) -> URL? {
        (task.originalRequest ?? task.currentRequest)?.url
    }

    /// Starts a transfer (`name`: a download's id, or a book's file), carrying on from where it got to when that was
    /// kept. A transfer runs apart from the client, sign-in and all, so it's made only once the address it goes to
    /// may be sent the sign-in: the address in use, and a home address only once it has said who it is (see
    /// `ServerAddresses`; on someone else's network something else may answer there). Where it got to is used only
    /// when it was made under the network rule that holds now and for that same address (resume data replays its own
    /// request); otherwise it starts afresh. Not allowed, or no longer wanted by the time that's known (`wanted`):
    /// nothing is made, and `refused` puts it back to waiting for the next try.
    private func startTransfer(_ name: String, request: URLRequest, wifiOnly: Bool, client: FlexarrClient,
                               wanted: @escaping @MainActor () -> Bool, refused: @escaping @MainActor () -> Void) {
        // Once at a time: a second ask while the address is being checked would make a second transfer.
        guard startingTransfers.insert(name).inserted else { return }
        let kept = resumeData.removeValue(forKey: name)
        Task { @MainActor in
            defer { startingTransfers.remove(name) }
            let allowed = await client.maySendTransfer(to: request.url)
            guard allowed, wanted() else {
                if let kept, resumeData[name] == nil { resumeData[name] = kept }
                if !allowed {
                    log.info("Holding a download until the server's address says who it is")
                    refused()
                }
                return
            }
            let resumed = kept.flatMap { $0.wifiOnly == wifiOnly && FlexarrClient.resumes($0.url, at: request.url) ? $0.data : nil }
            let task = resumed.map { transferSession.downloadTask(withResumeData: $0) } ?? transferSession.downloadTask(with: request)
            task.taskDescription = name
            task.resume()
        }
    }

    /// A transfer stopped for want of Wi-Fi or cut off: back to waiting (the server still has it ready), to carry on
    /// later (from where it got to, when that's known, at the address it was going to).
    private func held(_ id: String, resumeData data: Data?, wifiOnly: Bool, url: URL? = nil) {
        if let data { resumeData[id] = Resumable(data: data, wifiOnly: wifiOnly, url: url) }
        update(id) { if case .downloading = $0.state { $0.state = .preparing(1) } }
    }

    /// Catch-ups run one at a time: one asked for while another runs follows it, once.
    @ObservationIgnored private let catchUps = OneRunAtATime()

    /// Sends what was watched offline, follows what the server is getting ready, deletes what's finished with, and
    /// fetches next episodes that are due. One at a time: asked for during one, it runs again once that's done.
    func catchUp() async {
        await catchUps.run { [weak self] in await self?.catchUpNow() }
    }

    private func catchUpNow() async {
        deleteFinished()
        guard let client, isOnline else { return }
        // Only the attached profile's: another's would be refused (404) and dropped, so they wait for a sign-in as theirs.
        await tellDue(with: client)
        var sent = false
        for download in all where download.profileId == profileId {
            guard download.unsent != nil else { continue }
            if await deliverUnsent(of: download.id, with: client) { sent = true }
        }
        if await sendKeptReports(with: client, server: server, profileId: attachedProfileId) { sent = true }
        // Every screen showing what was watched (Continue watching, a show's next up, the watched marks) loads again,
        // once for all of them.
        if sent { onReportsSent?() }
        for download in all where download.profileId == profileId {
            guard let speed = download.book?.unsentSpeed else { continue }
            do {
                try await client.setPlaybackSpeed(itemId: download.item.id, speed: speed)
                update(download.id) { if $0.book?.unsentSpeed == speed { $0.book?.unsentSpeed = nil } }
            } catch {
                log.warning("Couldn't send a book's speed", error: error)
            }
        }
        await takeServerPlaces()
        deleteFinished() // podcast episodes whose play was waiting to be sent, or finished elsewhere
        await deletePlayedPodcasts()
        await removeWithdrawn()
        // A title saved by an app that didn't know its kind (a podcast episode, kept by an older version) was saved as
        // "unknown" and would stay so, filed under Films: what the server says now puts it right.
        for download in all where download.profileId == profileId && download.item.kind == .unknown {
            if let remote = try? await client.download(download.id), remote.item.kind != .unknown {
                update(download.id) { $0.item = remote.item }
            }
        }
        for download in downloads where download.isDownloaded {
            fetchNext(after: download)
        }
        await recoverDroppedTransfers()
        poll()
        resumeBooks()
        await reconcilePodcasts()
    }

    /// Tells the server what it's still to hear of the attached profile's downloads: ones deleted here, then ones that
    /// have come. Out of reach, the rest wait for the next catch-up; one the server hasn't heard of (404) is let go.
    private func tellDue(with client: FlexarrClient) async {
        guard let profile = attachedProfileId else { return }
        func tell(_ kind: String, _ entries: () -> Set<String>, forget: (String) -> Void,
                  _ send: (_ entry: String, _ id: String) async throws -> Void) async -> Bool {
            for (entry, id) in ProfileTells.due(entries(), profileId: profile) {
                // Another profile attached meanwhile: what's due has changed.
                guard attachedProfileId == profile else { return false }
                // Several rounds run at once (each delete starts one): one told meanwhile, or being told now, goes once.
                // Told again once forgotten, a delete the app made would go as by hand.
                let key = kind + entry
                guard entries().contains(entry), !tellsInFlight.contains(key) else { continue }
                tellsInFlight.insert(key)
                defer { tellsInFlight.remove(key) }
                do {
                    try await send(entry, id)
                    forget(entry)
                } catch FlexarrClientError.http(status: 404, _) {
                    forget(entry)
                } catch {
                    return false // still out of reach
                }
            }
            return true
        }
        guard await tell("delete:", { unsentDeletes }, forget: forgetDelete, { entry, id in
            try await client.deleteDownload(id, byHand: !unsentAutomaticDeletes.contains(entry))
        }) else { return }
        _ = await tell("received:", { unsentReceipts }, forget: { unsentReceipts.remove($0) }, { try await client.markDownloadReceived($1) })
    }

    /// What `tellDue` is telling the server right now, so overlapping rounds don't tell it twice.
    @ObservationIgnored private var tellsInFlight: Set<String> = []

    private func forgetDelete(_ entry: String) {
        unsentDeletes.remove(entry)
        unsentAutomaticDeletes.remove(entry)
    }

    /// A failed download goes to be asked for again ("Try again"): taken off by the app, not by hand, and the server
    /// told before it's asked again, so asking doesn't find the old one there, about to go. Out of reach, it's told
    /// later. `client`: the sign-in it's asked again with (a batch's own).
    func dropFailed(_ id: String, with client: FlexarrClient? = nil) async {
        guard let download = all.first(where: { $0.id == id }), case .failed = download.state else { return }
        delete(id, tellNow: false)
        // A book's download is the phone's alone: nothing to tell.
        guard download.book == nil, let client = client ?? self.client, attachedProfileId == download.profileId else { return }
        let entry = ProfileTells.entry(id, profileId: download.profileId)
        do {
            try await client.deleteDownload(id, byHand: false)
            forgetDelete(entry)
        } catch FlexarrClientError.http(status: 404, _) {
            forgetDelete(entry)
        } catch {
            // out of reach: the next catch-up tells it
        }
    }

    /// Try again, for a download that failed: asked for once more as it was (the same size, as the same profile), the
    /// old one taken off first as the app's own doing. A title's page and the Downloads list both come here, so trying
    /// again is the same wherever it's pressed. A book is fetched again from its manifest.
    func retry(_ id: String) async throws {
        guard let download = all.first(where: { $0.id == id }), case .failed = download.state else { return }
        // Someone else is watching now: not theirs to ask for.
        guard download.profileId == profileId else { throw CancellationError() }
        if download.book != nil {
            try await self.download(book: download.item)
        } else {
            await dropFailed(id)
            try await self.download(itemId: download.item.id, quality: download.quality)
        }
    }

    /// Try again, for an album or playlist: each of its tracks that failed (or was never asked for) is asked for again,
    /// the rest left as they are.
    func retry(collectionId: String) async throws {
        guard let collection = collection(for: collectionId) else { return }
        try await fetchTracks(of: collection, ids: collection.trackIds)
    }

    /// This profile's downloads in Downloads that failed: films, episodes, books and podcast episodes, then albums and
    /// playlists (whose tracks show under them, not one by one).
    var failedDownloads: (downloads: [LocalDownload], collections: [LocalCollection]) {
        let rows = (videoDownloads + bookDownloads + podcastDownloads).filter(\.hasFailed)
        let albums = musicCollections.filter { if case .failed = status(of: $0) { true } else { false } }
        return (rows, albums)
    }

    /// Try all again: every failed download in Downloads, one after another. The first problem is said once all have
    /// been asked for.
    func retryAllFailed() async throws {
        let failed = failedDownloads
        var problem: Error?
        for download in failed.downloads {
            do { try await retry(download.id) } catch is CancellationError { throw CancellationError() } catch { problem = problem ?? error }
        }
        for collection in failed.collections {
            do { try await retry(collectionId: collection.id) } catch is CancellationError { throw CancellationError() } catch { problem = problem ?? error }
        }
        if let problem { throw problem }
    }

    /// Downloads this profile may no longer play: a title a parent has since blocked, put above a lowered age limit or
    /// hidden the library of, and one the server no longer has. The server says which (`OfflineCheck`), and they go,
    /// books too, unless one's playing or has a play here still to send (the next catch-up tries again). One whose
    /// file the server can't find just now (a drive offline) stays. A server before the check, or out of reach, leaves
    /// them all.
    private func removeWithdrawn() async {
        guard let client, isOnline, let profileId, attachedProfileId == profileId else { return }
        let kept = downloads.filter { $0.isDownloaded || $0.book != nil }
        guard !kept.isEmpty, let answer = try? await client.offlineCheck(itemIds: kept.map(\.item.id)) else { return }
        // The profile may have changed while the server answered: the answer is only for the one asked about.
        guard self.client != nil, self.profileId == profileId, attachedProfileId == profileId else { return }
        let remove = answer.toRemove(asked: kept.map(\.item.id))
        for download in kept where remove.contains(download.item.id.lowercased()) {
            guard let now = all.first(where: { $0.id == download.id }), now.unsent == nil, !isPlaying(now.item.id) else { continue }
            log.info("Removing a download this profile may no longer play: \(now.item.title)")
            delete(now.id)
        }
    }

    /// Films, episodes and tracks shown as downloading with no transfer behind them (the system dropped it with the
    /// app, or a guest's transfer ended when the app did): back to waiting, so the next look at the server fetches them
    /// again, from where they'd got to when that was kept. Books aren't here: `resumeBooks` restarts their missing files.
    private func recoverDroppedTransfers() async {
        // Only ones already downloading before the system's list is asked for: a transfer started meanwhile isn't in it.
        let candidates = Set(all.filter { download in
            guard download.book == nil, case .downloading = download.state else { return false }
            return true
        }.map(\.id))
        guard !candidates.isEmpty else { return }
        let running = await runningTransfers()
        for id in candidates.subtracting(running) {
            guard let download = all.first(where: { $0.id == id }), case .downloading = download.state else { continue }
            log.info("Picking up a download the system dropped: \(download.item.title)")
            held(id, resumeData: nil, wifiOnly: false)
        }
    }

    /// The names (`taskDescription`) of every transfer under way, in either session.
    private func runningTransfers() async -> Set<String> {
        let sessions = madeGuestSession || GuestMode.isActive ? [session!, guestSession] : [session!]
        var names = Set<String>()
        for session in sessions {
            let found: [String] = await withCheckedContinuation { continuation in
                session.getAllTasks { tasks in continuation.resume(returning: tasks.compactMap(\.taskDescription)) }
            }
            names.formUnion(found)
        }
        return names
    }

    /// Where each download's title has got to on the server (watched on the TV since, say), taken by the ones with no
    /// place of their own still to send, so a download starts from there and its next report doesn't put the old
    /// place back. Out of reach, or a server that can't list them, leaves them as they are.
    private func takeServerPlaces() async {
        guard let client, isOnline, let profileId, downloads.contains(where: Self.followsServerPlace),
              let remote = try? await client.downloads() else { return }
        guard self.profileId == profileId else { return }
        for download in remote { takeServerPlace(download.item, for: download.id) }
    }

    /// Films, episodes and podcast episodes: tracks keep no place, and a book's is its manifest's.
    nonisolated private static func followsServerPlace(_ download: LocalDownload) -> Bool {
        download.book == nil && [.movie, .episode, .podcastEpisode].contains(download.item.kind)
    }

    /// Takes the server's place for one download (`item` as the server has it now), unless this phone has its own
    /// still to send, or it's playing. Watched there, it's finished here too (from the start); otherwise it carries on
    /// from the server's place, or the start where that was forgotten.
    private func takeServerPlace(_ item: ItemSummary, for id: String) {
        guard let local = all.first(where: { $0.id == id && $0.profileId == profileId }), Self.followsServerPlace(local),
              local.unsent == nil, !isPlaying(local.item.id) else { return }
        let state = item.userData
        update(id) {
            if state.watched {
                guard $0.finishedAt == nil else { return }
                $0.position = 0
                $0.finishedAt = Date()
                $0.placeHeardHere = false
            } else {
                let place = max(0, state.positionSeconds)
                guard $0.position != place || $0.finishedAt != nil else { return }
                $0.position = place
                $0.finishedAt = nil
                $0.placeHeardHere = false
            }
        }
    }

    /// How to play a download, its place first brought up to date from the server when it's in reach (watched on
    /// the TV since it was last looked at, say). A slow answer isn't waited for long.
    func freshPlaybackInfo(for download: LocalDownload, preferences: PlaybackPreferences?) async -> PlaybackInfo? {
        if let client, isOnline, Self.followsServerPlace(download), download.unsent == nil {
            let id = download.id
            let remote = await withTaskGroup(of: OfflineDownload?.self) { group in
                group.addTask { try? await client.download(id) }
                group.addTask { try? await Task.sleep(for: .seconds(3)); return nil }
                let first = await group.next() ?? nil
                group.cancelAll()
                return first
            }
            if let remote { takeServerPlace(remote.item, for: id) }
        }
        return playbackInfo(for: all.first { $0.id == download.id } ?? download, preferences: preferences)
    }

    /// Listening done with no connection: a book's place goes to its progress with the time it was heard (the newest
    /// wins on the server); anything else's as offline progress.
    private func send(_ unsent: UnsentProgress, itemId: String, isBook: Bool, with client: FlexarrClient) async throws {
        if isBook {
            try await client.reportBookProgress(itemId: itemId, BookProgressUpdate(positionSeconds: unsent.position, device: MusicPlayer.deviceName,
                                                                                   recordedAt: unsent.playedAt, ended: unsent.finished))
        } else {
            try await client.reportOfflineProgress(itemId: itemId, positionSeconds: unsent.position, finished: unsent.finished, playedAt: unsent.playedAt)
        }
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

    /// Marked played here: the download is the same episode or book, so it's finished too, from the start, with no place
    /// of its own left to send. A podcast episode's download goes at once, or a show's when all its episodes were; a book
    /// goes on as one heard to the end does (the next in its series, deleted if Settings says so).
    func markedPlayed(itemId: String) {
        for download in downloads where download.item.kind == .podcastEpisode && (download.item.id == itemId || download.item.parentId == itemId) {
            if !isPlaying(download.item.id) { delete(download.id) }
        }
        // A whole show or season: each of its downloaded episodes is finished too.
        for download in downloads where download.item.kind == .episode && (download.item.seriesId == itemId || download.item.parentId == itemId) {
            finishHere(download.id)
        }
        guard let download = entry(for: itemId), download.item.kind != .podcastEpisode else { return }
        finishHere(download.id)
        if download.book != nil { bookFinished(itemId: itemId) }
    }

    /// Finished, from the start, with no place of its own left to send.
    private func finishHere(_ id: String) {
        update(id) {
            $0.position = 0
            $0.unsent = nil
            $0.finishedAt = $0.finishedAt ?? Date()
            if let manifest = $0.book?.manifest { $0.book?.manifest = manifest.withProgress(BookProgress(positionSeconds: 0, finished: true, finishedAt: Date())) }
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
            // Part-heard here, it stays; a place only taken from the server (heard on another device) is no reason to.
            if download.unsent == nil, download.finishedAt != nil || download.position == 0 || download.placeHeardHere == false { delete(download.id) }
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
        // An episode deleted by hand isn't fetched again as the next one (asked for by hand, it comes again).
        if let download = all.first(where: { $0.id == id }), download.item.kind == .episode {
            skippedNext.insert(download.item.id)
        }
        delete(id, byHand: true)
    }

    // MARK: Downloading

    /// The sign-in and profile a download is asked for with: a batch (Download show) keeps the ones it started with.
    typealias Owner = (client: FlexarrClient, profileId: String)

    /// Who's signed in now, to ask for downloads as.
    var owner: Owner? {
        guard let client, let profileId else { return nil }
        return (client, profileId)
    }

    /// Whether `owner`'s profile is still the one watching, and still signed in.
    func isStill(_ owner: Owner) -> Bool { client != nil && profileId == owner.profileId }

    /// Downloads a title at the size chosen in Settings. An original the phone can't play comes at High instead.
    /// `owner`: whose it is, when not whoever is signed in now (a batch's: it's asked for as the profile that started
    /// it, and not at all once someone else is watching).
    func download(itemId: String, as owner: Owner? = nil) async throws {
        guard let client = owner?.client ?? client else { throw FlexarrClientError.invalidResponse }
        var quality = quality
        if quality == .original, let options = try? await client.downloadOptions(itemId),
           options.first(where: { $0.quality == .original })?.available == false {
            quality = .high
        }
        try await download(itemId: itemId, quality: quality, as: owner)
    }

    /// Asks the server to get a title ready at this size, then fetches it when it is.
    func download(itemId: String, quality: DownloadQuality, automatic: Bool = false, as owner: Owner? = nil) async throws {
        guard let client = owner?.client ?? client, let profileId = owner?.profileId ?? profileId else {
            throw FlexarrClientError.invalidResponse
        }
        // Someone else is watching now: not theirs to ask for (and what's here is looked up as theirs).
        if owner != nil, profileId != self.profileId { throw CancellationError() }
        // Already here or on its way: nothing to do. Failed: start again.
        if let existing = entry(for: itemId) {
            guard case .failed = existing.state else { return }
            await dropFailed(existing.id, with: client)
        }
        let remote = try await client.requestDownload(itemId, quality: quality)
        // Handed back by the server, it's alive there: a delete of it still to send (one that didn't get through before
        // trying again) would take away the download just asked for.
        forgetDelete(ProfileTells.entry(remote.id, profileId: profileId))
        if !automatic { skippedNext.remove(itemId) }
        // Already asked for, through a linked copy (or by another press meanwhile): the one here is kept as it is (a
        // transfer under way carries on rather than starting again from nothing), and found under this id too.
        if let held = all.first(where: { $0.id == remote.id && $0.profileId == profileId }) {
            let failed = if case .failed = held.state { true } else { false }
            if !failed {
                if !held.isDownload(of: itemId) { update(held.id) { $0.askedAs = ($0.askedAs ?? []) + [itemId] } }
                return
            }
        }
        var local = LocalDownload(id: remote.id, profileId: profileId, quality: quality, item: remote.item, markers: remote.markers,
                                  durationSeconds: remote.durationSeconds, nextId: remote.nextId, state: .preparing(remote.progress),
                                  sizeBytes: remote.sizeBytes, automatic: automatic, manual: automatic ? nil : true)
        if remote.item.id != itemId { local.askedAs = [itemId] }
        // Part way through already (on this phone or anywhere else): the download carries on from there, as playing it
        // from the server would, rather than from the start.
        if !remote.item.userData.watched {
            local.position = max(0, remote.item.userData.positionSeconds)
            local.placeHeardHere = false
        }
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

    // MARK: Books

    /// A book's download, and its folder: the profile's own, so two profiles can each keep the same book (books kept
    /// before this have "book-<item>", which still works: everything finds them by the id saved with them).
    nonisolated private static func bookDownloadId(_ itemId: String, profileId: String) -> String {
        let safe = { (part: String) in String(part.map { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" ? $0 : "_" }) }
        return "book-\(safe(profileId))-\(safe(itemId))"
    }

    /// Downloads a whole book: its manifest from the server (the files as they are, or converted where this phone
    /// can't play their sound), then each file into one folder, all under one entry.
    func download(book item: ItemSummary, automatic: Bool = false) async throws {
        guard let client, let profileId else { throw FlexarrClientError.invalidResponse }
        if let existing = entry(for: item.id) {
            guard case .failed = existing.state else { return }
            delete(existing.id)
        }
        let manifest = try await client.bookManifest(item.id, codecs: MusicPlayer.bookCodecs)
        let id = Self.bookDownloadId(item.id, profileId: profileId)
        let folder = id
        try? FileManager.default.createDirectory(at: Self.folder.appending(path: folder, directoryHint: .isDirectory), withIntermediateDirectories: true)
        var local = LocalDownload(id: id, profileId: profileId, quality: .original, item: item, markers: [], durationSeconds: manifest.durationSeconds,
                                  nextId: manifest.nextInSeries?.id, state: .downloading(0), sizeBytes: manifest.files.reduce(0) { $0 + $1.size },
                                  fileName: folder, position: manifest.progress.finished ? 0 : manifest.progress.positionSeconds,
                                  automatic: automatic, manual: automatic ? nil : true)
        local.book = LocalBook(manifest: manifest, files: Array(repeating: nil, count: manifest.files.count))
        all.removeAll { $0.id == id }
        all.append(local)
        save()
        log.info("Downloading the book \(item.title): \(manifest.files.count) files")
        if let image = await fetchImage(at: item.artwork.poster, named: "\(id).jpg", width: 600) {
            update(id) { $0.imageFileName = image }
        }
        startBookFiles(id)
    }

    /// Starts every file of a book that isn't here and isn't on its way.
    private func startBookFiles(_ id: String) {
        guard let client, let download = all.first(where: { $0.id == id }), let book = download.book else { return }
        let wifiOnly = isWiFiOnly(download)
        if wifiOnly, !isOnWiFi { return }
        allTasks { tasks in
            // A transfer being cancelled isn't under way. One made Wi-Fi only before that was turned off would wait
            // for Wi-Fi for ever off it (a transfer's network rule can't change): made again under the rule now.
            let live = tasks.filter { $0.state == .running || $0.state == .suspended }
            let stranded = live.filter { $0.taskDescription?.hasPrefix("\(id)#") == true && Self.madeWiFiOnly($0) && !wifiOnly }
            let running = Set(live.compactMap(\.taskDescription))
            Task { @MainActor in
                var restarted = Set<String>()
                if !self.isOnWiFi {
                    for task in stranded {
                        guard let name = task.taskDescription else { continue }
                        task.cancel()
                        self.resumeData[name] = nil
                        restarted.insert(name)
                    }
                }
                for (index, file) in book.manifest.files.enumerated() where book.files[index] == nil {
                    let name = "\(id)#\(index)"
                    guard !running.contains(name) || restarted.contains(name), var request = client.bookFileRequest(file) else { continue }
                    if wifiOnly {
                        request.allowsCellularAccess = false
                        request.allowsExpensiveNetworkAccess = false
                        request.allowsConstrainedNetworkAccess = false
                    }
                    // Still missing once the address has been checked (not deleted, nor arrived meanwhile); refused, it's
                    // started again at the next catch-up (`resumeBooks`).
                    self.startTransfer(name, request: request, wifiOnly: wifiOnly, client: client, wanted: {
                        guard let files = self.all.first(where: { $0.id == id })?.book?.files, files.indices.contains(index) else { return false }
                        return files[index] == nil
                    }, refused: {})
                }
            }
        }
    }

    /// Books still missing files (cut off, or waiting for Wi-Fi) carry on.
    private func resumeBooks() {
        for download in downloads where download.book != nil && !download.isDownloaded {
            if case .failed = download.state { continue }
            startBookFiles(download.id)
        }
    }

    /// A book was heard to the end: its download may go (Settings), and the next in its series may come.
    func bookFinished(itemId: String) {
        // Only a book that was downloaded: the series is being listened to on this phone.
        guard let download = entry(for: itemId), download.book != nil else { return }
        update(download.id) { $0.finishedAt = $0.finishedAt ?? Date() }
        fetchNextBook(after: itemId)
        deleteFinished()
    }

    private func fetchNextBook(after itemId: String) {
        guard downloadsNextBook, isOnWiFi || !autoDownloadsOnWiFiOnly, let client, fetchingNextBook.insert(itemId).inserted else { return }
        Task {
            defer { fetchingNextBook.remove(itemId) }
            guard let next = try? await client.bookManifest(itemId, codecs: MusicPlayer.bookCodecs).nextInSeries, entry(for: next.id) == nil else { return }
            try? await download(book: next, automatic: true)
        }
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
        try await fetchTracks(of: collection, ids: collection.trackIds)
    }

    /// Asks for each of a collection's tracks (those here or on their way stay as they are; failed ones start again).
    /// One track the server can't offer doesn't stop the rest: each is asked for, and the first problem is reported
    /// once all have been.
    private func fetchTracks(of collection: LocalCollection, ids: [String]) async throws {
        queueing.insert(collection.id)
        defer { queueing.remove(collection.id) }
        var problem: Error?
        for id in ids {
            do {
                try await download(itemId: id, quality: .original)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                log.warning("Couldn't download a track of \(collection.title)", error: error)
                problem = problem ?? error
            }
        }
        if let problem { throw problem }
    }

    /// Deletes a collection and its tracks, except tracks another collection still holds.
    func remove(collectionId: String) {
        guard let collection = collection(for: collectionId) else { return }
        let kept = Set(collections.filter { $0.id != collection.id && $0.profileId == collection.profileId }.flatMap(\.trackIds))
        for trackId in collection.trackIds where !kept.contains(trackId) {
            if let track = entry(for: trackId) { delete(track.id, byHand: true) }
        }
        collections.removeAll { $0.id == collection.id && $0.profileId == collection.profileId }
        saveCollections()
        removeImageIfUnused(collection.imageFileName)
    }

    /// The album's cover, saved once for its tracks and the album itself.
    nonisolated private static func coverName(forAlbum albumId: String) -> String { "cover-\(albumId).jpg" }

    /// Follows the server getting downloads ready, every few seconds while any are.
    /// Only the first few waiting are asked after on every look, in the order they were asked for (the order the server
    /// works through them), and the rest a couple at a time (`DownloadPolling`): a whole show waiting would otherwise
    /// be a question per episode every few seconds.
    private func poll() {
        guard polling == nil else { return }
        polling = Task { [weak self] in
            while let self, !Task.isCancelled {
                // Ones waiting for Wi-Fi needn't be asked after until there is some.
                // Only this profile's: another profile's can't be asked after with this sign-in.
                let waiting = all.filter {
                    guard $0.profileId == profileId, case .preparing = $0.state else { return false }
                    return isOnWiFi || !isWiFiOnly($0)
                }.sorted { $0.createdAt < $1.createdAt }
                guard !waiting.isEmpty, let client else { break }
                let ids = waiting.map(\.id)
                let now = Date()
                // Heard of now (asked for just now, or found waiting at launch): counts as just asked.
                let known = Set(ids)
                lastAsked = lastAsked.filter { known.contains($0.key) }
                for id in ids where lastAsked[id] == nil { lastAsked[id] = now }
                for id in DownloadPolling.due(ids, lastAsked: lastAsked, now: now) {
                    lastAsked[id] = Date()
                    do {
                        apply(try await client.download(id))
                    } catch FlexarrClientError.http(status: 404, _) {
                        // The server has no record of it (deleted there, or a server set up again): it would never come.
                        update(id) { $0.state = .failed(t("The server doesn’t have this download any more. Download it again.")) }
                    } catch {
                        // Out of reach for now: asked again at the next look.
                    }
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
            let id = remote.id
            // Still downloading once the address has been checked: not deleted, nor put back to waiting meanwhile (a
            // second transfer would be made then). Refused: back to waiting, asked about again at the next look.
            startTransfer(id, request: request, wifiOnly: wifiOnly, client: client, wanted: { [weak self] in
                guard let download = self?.all.first(where: { $0.id == id }), case .downloading = download.state else { return false }
                return true
            }, refused: { [weak self] in
                self?.held(id, resumeData: nil, wifiOnly: wifiOnly)
                self?.poll()
            })
        case .removed where !local.isDownloaded:
            // Deleted on another of this profile's phones: nothing to fetch.
            all.removeAll { $0.id == remote.id }
            save()
        case .received where !local.isDownloaded, .expired:
            update(remote.id) { $0.state = .failed(t("This download ran out on the server. Download it again.")) }
        case .failed:
            update(remote.id) { $0.state = .failed(remote.error ?? t("The server couldn’t get this ready.")) }
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
        guard let url = client?.imageURL(path, width: width), let (data, _) = try? await URLSession.shared.data(for: GuestTunnelToken.request(url)), PlatformImage(data: data) != nil else {
            return nil
        }
        try? data.write(to: Self.folder.appending(path: name))
        images.removeObject(forKey: name as NSString)
        return name
    }

    // MARK: Deleting

    /// Takes a download off the phone. `byHand`: someone chose to, rather than the app by itself (keeping only the newest
    /// few, once played, signing out, trying again). `tellNow`: false when the caller tells the server itself.
    func delete(_ id: String, byHand: Bool = false, tellNow: Bool = true) {
        guard let download = all.first(where: { $0.id == id }) else { return }
        allTasks { tasks in
            // A book's files are its id with "#" and the file's number.
            for task in tasks where task.taskDescription == id || task.taskDescription?.hasPrefix("\(id)#") == true { task.cancel() }
        }
        if let name = download.fileName {
            try? FileManager.default.removeItem(at: Self.folder.appending(path: name))
        }
        all.removeAll { $0.id == id }
        unsentReceipts = unsentReceipts.filter { ProfileTells.parse($0).id != id }
        progressShownAt[id] = nil
        save(rightAway: true) // its file has gone
        removeImageIfUnused(download.imageFileName)
        resumeData[id] = nil
        // A book's download is the phone's alone: the server keeps no copy to drop.
        if download.book != nil {
            bookFileProgress[id] = nil
            for key in resumeData.keys where key.hasPrefix("\(id)#") { resumeData[key] = nil }
            return
        }
        // The server drops its copy (and, for a podcast episode, won't offer it again), told as the download's own
        // profile: out of reach, or someone else's, it's told later.
        let entry = ProfileTells.entry(id, profileId: download.profileId)
        unsentDeletes.insert(entry)
        if byHand { unsentAutomaticDeletes.remove(entry) } else { unsentAutomaticDeletes.insert(entry) }
        if tellNow, let client, attachedProfileId == download.profileId {
            Task { await tellDue(with: client) }
        }
    }

    /// A picture goes once nothing shows it any more (an album's cover is shared by its tracks).
    private func removeImageIfUnused(_ name: String?) {
        guard let name, !all.contains(where: { $0.imageFileName == name }), !collections.contains(where: { $0.imageFileName == name }) else { return }
        try? FileManager.default.removeItem(at: Self.folder.appending(path: name))
        images.removeObject(forKey: name as NSString)
    }

    /// The server the downloads were last attached to (see `UnsentReports.serverKey`), remembered so what's kept from
    /// downloads deleted by signing out is known to be its.
    @ObservationIgnored private var server: String? = UserDefaults.standard.string(forKey: "downloads.server")
    /// The profile that was watching when the downloads were deleted by signing out: whose reports the sign-out sends.
    @ObservationIgnored private var signedOutProfileId: String?
    /// Screens reload: something watched offline has reached the server.
    @ObservationIgnored var onReportsSent: (() -> Void)?

    private static let keptReportsFile = folder.appending(path: "unsent-reports.json")
    /// Watched offline and not yet sent, kept from downloads that have gone (signing out, Delete all) until it's sent
    /// to the server and profile it was watched as: after the next sign-in there, however long that is.
    @ObservationIgnored private var keptReports: [UnsentReport] = SavedList.load(UnsentReport.self, from: DownloadStore.keptReportsFile).items {
        didSet {
            guard keptReports != oldValue else { return }
            if keptReports.isEmpty {
                try? FileManager.default.removeItem(at: Self.keptReportsFile)
            } else if let data = try? Self.makeEncoder().encode(keptReports) {
                try? data.write(to: Self.keptReportsFile, options: .atomic)
            }
        }
    }

    /// Signing out: sends what this profile watched offline and not yet sent, with the sign-in that's still working,
    /// whether or not the downloads have been deleted yet. What doesn't go (out of reach, or another profile's, which
    /// would land in the wrong history) is kept for the next sign-in to this server as that profile.
    func sendLeftoverProgress(with client: FlexarrClient) async {
        let owner = attachedProfileId ?? profileId ?? signedOutProfileId
        signedOutProfileId = nil
        for download in all where download.profileId == owner && download.unsent != nil {
            await deliverUnsent(of: download.id, with: client)
        }
        await sendKeptReports(with: client, server: UnsentReports.serverKey(client.addresses.home), profileId: owner)
    }

    /// Sends the kept reports that are this server's and profile's. True when any reached it. Stops at the first that
    /// can't be sent for want of the server, leaving the rest for the next catch-up.
    @discardableResult
    private func sendKeptReports(with client: FlexarrClient, server: String?, profileId: String?) async -> Bool {
        var sent = false
        for report in UnsentReports.due(in: keptReports, server: server, profileId: profileId) {
            let unsent = UnsentProgress(position: report.position, finished: report.finished, playedAt: report.playedAt)
            do {
                try await send(unsent, itemId: report.itemId, isBook: report.isBook, with: client)
                keptReports = UnsentReports.removing(report, from: keptReports)
                sent = true
            } catch FlexarrClientError.http(status: 404, _) {
                if let count = UnsentReports.notFoundAgain(report.notFound) {
                    var counted = report
                    counted.notFound = count
                    keptReports = keptReports.map { $0 == report ? counted : $0 }
                } else {
                    keptReports = UnsentReports.removing(report, from: keptReports)
                }
            } catch {
                log.warning("Couldn't send offline progress kept from a deleted download", error: error)
                break
            }
        }
        return sent
    }

    /// What's still to send of the downloads about to go, kept apart (see `keptReports`). Nothing is kept without knowing
    /// whose server it was: sent to another, the main profile's would land in that server's history.
    private func keepUnsentReports() {
        guard let server else { return }
        let reports = all.compactMap { download in
            download.unsent.map {
                UnsentReport(server: server, profileId: download.profileId, itemId: download.item.id, position: $0.position,
                             finished: $0.finished, playedAt: $0.playedAt, isBook: download.book != nil, notFound: $0.notFound ?? 0)
            }
        }
        if !reports.isEmpty { keptReports = UnsentReports.adding(reports, to: keptReports) }
    }

    /// Every download goes, whoever's it was (signing out, or Settings' Delete all; the server keeps its record of what
    /// was declined). What was watched on them and not yet sent stays, to be sent (see `keptReports`).
    func deleteAll() {
        keepUnsentReports()
        signedOutProfileId = profileId ?? signedOutProfileId
        for id in all.map(\.id) { delete(id) }
        let pictures = collections.compactMap(\.imageFileName)
        collections = []
        saveCollections()
        for name in pictures { removeImageIfUnused(name) }
        // Signed out: the emptied list is on disk before anything else happens.
        manifestWriter.flush()
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
            case .audiobook where removesFinishedBooks && !isPlaying(download.item.id): delete(download.id)
            default: break
            }
        }
    }

    // MARK: Next episode

    /// Keeps one episode ahead: when one's downloaded or watched, fetch the next, on Wi-Fi, at the same size. One that
    /// was itself fetched this way is the episode ahead, and brings the one after only once it's been watched
    /// (otherwise each would bring the next, to the end of the show); and one deleted by hand isn't brought back.
    private func fetchNext(after download: LocalDownload) {
        // Another profile's (it came while someone else was watching): asked for now, it would be theirs. Its own
        // catch-up fetches it.
        guard downloadsNext, isOnWiFi || !autoDownloadsOnWiFiOnly, download.profileId == attachedProfileId,
              let nextId = NextEpisodeRule.nextToFetch(kind: download.item.kind, isDownloaded: download.isDownloaded, automatic: download.automatic,
                                                        watched: download.finishedAt != nil, nextId: download.nextId, skipped: skippedNext),
              entry(for: nextId) == nil else { return }
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
            $0.placeHeardHere = true
            $0.unsent = UnsentProgress(position: position, finished: finished, playedAt: Date())
        }
        if finished, let updated = entry(for: itemId) { fetchNext(after: updated) }
        // A place saved part way (every few seconds while it plays) goes on its own; the end of it brings everything
        // up to date (what's finished with is deleted once that's sent).
        let id = download.id
        let sending = Task { await sendProgress(of: id) }
        reportsSent = sending
        if finished { Task { await sending.value; await catchUp() } }
    }

    /// Sends one download's place, saved while it played, if there's one to send and the server's in reach.
    private func sendProgress(of id: String) async {
        guard let client, isOnline, let download = all.first(where: { $0.id == id }), download.profileId == profileId,
              download.unsent != nil else { return }
        await deliverUnsent(of: id, with: client)
    }

    /// Sends one download's unsent place. True once the server has it. A title the server says it hasn't is tried a
    /// few times (on separate catch-ups) and then let go; anything else keeps it for the next catch-up.
    @discardableResult
    private func deliverUnsent(of id: String, with client: FlexarrClient) async -> Bool {
        guard let download = all.first(where: { $0.id == id }), let unsent = download.unsent else { return false }
        do {
            try await send(unsent, itemId: download.item.id, isBook: download.book != nil, with: client)
            update(id) { if $0.unsent == unsent { $0.unsent = nil } }
            // Deleted meanwhile (signing out while it was on its way): what was kept from it has gone too.
            keptReports.removeAll { $0.profileId == download.profileId && $0.itemId == download.item.id && $0.playedAt <= unsent.playedAt }
            return true
        } catch FlexarrClientError.http(status: 404, _) {
            let count = UnsentReports.notFoundAgain(unsent.notFound ?? 0)
            if count == nil { log.warning("The server has no such title: dropping the offline progress of \(download.item.title)") }
            update(id) {
                guard $0.unsent == unsent else { return }
                if let count { $0.unsent?.notFound = count } else { $0.unsent = nil }
            }
        } catch {
            log.warning("Couldn't send offline progress", error: error)
        }
        return false
    }

    /// The report the last place recorded from a download went in, so the screens reload once the server has it.
    @ObservationIgnored private(set) var reportsSent: Task<Void, Never>?

    /// Where listening to a downloaded book got to: kept here, and sent with its time once there's a connection.
    func recordBook(itemId: String, position: Double, ended: Bool) {
        guard let download = entry(for: itemId), download.book != nil else { return }
        update(download.id) {
            $0.position = ended ? 0 : position
            if ended { $0.finishedAt = $0.finishedAt ?? Date() } else if position > 0 { $0.finishedAt = nil }
            $0.unsent = UnsentProgress(position: position, finished: ended, playedAt: Date())
        }
        if ended { bookFinished(itemId: itemId) }
        if isOnline { Task { if ended { await catchUp() } else { await sendProgress(of: download.id) } } }
    }

    func refreshBook(_ fresh: BookManifest) -> BookManifest? {
        guard let download = entry(for: fresh.itemId), let book = download.book else { return nil }
        let manifest = book.manifest.refreshed(from: fresh, speed: book.unsentSpeed)
        if manifest != book.manifest { update(download.id) { $0.book?.manifest = manifest } }
        return manifest
    }

    func recordBookSpeed(itemId: String, speed: Double) {
        guard let download = entry(for: itemId), let book = download.book else { return }
        update(download.id) {
            $0.book?.manifest = book.manifest.withSpeed(speed)
            $0.book?.unsentSpeed = speed
        }
        if isOnline { Task { await catchUp() } }
    }

    func isBookFinished(_ bookId: String) -> Bool {
        entry(for: bookId)?.finishedAt != nil
    }

    func localBook(for bookId: String) -> (manifest: BookManifest, files: [URL])? {
        guard let download = entry(for: bookId), download.isDownloaded, let book = download.book, book.isComplete, let folder = download.fileName else { return nil }
        let base = Self.folder.appending(path: folder, directoryHint: .isDirectory)
        return (book.manifest, book.files.compactMap { $0.map { base.appending(path: $0) } })
    }

    func forgetPosition(itemId: String) {
        guard let download = entry(for: itemId) else { return }
        update(download.id) {
            $0.position = 0
            $0.unsent = nil
            $0.finishedAt = nil
            if let manifest = $0.book?.manifest { $0.book?.manifest = manifest.withProgress(BookProgress(positionSeconds: 0, finished: false)) }
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

    func resumePosition(ofEpisode episode: ItemSummary) -> Double {
        guard let download = entry(for: episode.id), download.finishedAt == nil else { return 0 }
        // Where this phone got to, once it has played any of it: that's as new as anything it's been told (an episode
        // wound back here mustn't jump forward again to an older place). Until then, where the server says it was left,
        // which may be further on than when it was downloaded.
        if download.position > 0 || download.unsent != nil || episode.userData.watched { return download.position }
        return max(0, episode.userData.positionSeconds)
    }

    func nextDownloadedEpisode(after episode: ItemSummary) -> ItemSummary? {
        podcastDownloads
            .filter { $0.isDownloaded && $0.finishedAt == nil && $0.item.id != episode.id && $0.item.seriesId == episode.seriesId && $0.item.addedAt > episode.addedAt }
            .min { $0.item.addedAt < $1.item.addedAt }?.item
    }

    func localArtwork(for trackId: String) -> PlatformImage? {
        image(named: entry(for: trackId)?.imageFileName)
    }

    /// Saved pictures already read from disk, by name: rows ask for theirs every time they're drawn (on each step of a
    /// download's progress, say), and a playlist's mosaic for several.
    @ObservationIgnored private let images: NSCache<NSString, PlatformImage> = {
        let cache = NSCache<NSString, PlatformImage>()
        cache.countLimit = 120
        return cache
    }()

    /// A saved picture if it's been read already: never touches the disk, so a row can draw it straight away.
    func keptImage(named name: String?) -> PlatformImage? {
        name.flatMap { images.object(forKey: $0 as NSString) }
    }

    /// A saved picture, read and decoded off the main thread the first time (then kept), so a list's first scroll
    /// doesn't stall on it.
    func preparedImage(named name: String?) async -> PlatformImage? {
        guard let name else { return nil }
        if let kept = keptImage(named: name) { return kept }
        let path = Self.folder.appending(path: name).path(percentEncoded: false)
        let image = await Task.detached(priority: .userInitiated) { () async -> PlatformImage? in
            guard let image = PlatformImage(contentsOfFile: path) else { return nil }
            return await image.preparedForDisplay()
        }.value
        if let image { images.setObject(image, forKey: name as NSString) }
        return image
    }

    /// Downloads › Shows as last worked out: the episodes' ids it was from, and each show's id, title and episodes'
    /// ids in order (`downloadedShows`). Progress changes the episodes but not this, so it's looked up, not redone.
    @ObservationIgnored var showsLayout: (ids: [String], shows: [(id: String, title: String, episodeIds: [String])])?

    /// A saved picture, by the name a download or collection holds.
    func image(named name: String?) -> PlatformImage? {
        guard let name else { return nil }
        if let kept = images.object(forKey: name as NSString) { return kept }
        guard let image = PlatformImage(contentsOfFile: Self.folder.appending(path: name).path(percentEncoded: false)) else { return nil }
        images.setObject(image, forKey: name as NSString)
        return image
    }

    // MARK: Saving

    /// Changes a download, if that changes anything: setting `all` at all redraws every screen that lists downloads, and
    /// most answers about one being got ready say what was known already.
    private func update(_ id: String, _ change: (inout LocalDownload) -> Void) {
        guard let index = all.firstIndex(where: { $0.id == id }) else { return }
        var changed = all[index]
        change(&changed)
        guard changed != all[index] else { return }
        all[index] = changed
        save()
    }

    private func updateCollection(_ id: String, _ change: (inout LocalCollection) -> Void) {
        guard let index = collections.firstIndex(where: { $0.id == id && $0.profileId == profileId }) else { return }
        change(&collections[index])
        saveCollections()
    }

    private static let encoder = makeEncoder()

    nonisolated private static func makeEncoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }

    /// Whether the app is in front and in use, where it isn't suspended from one moment to the next.
    private var isInFront: Bool {
        #if canImport(UIKit)
        UIApplication.shared.applicationState == .active
        #else
        true
        #endif
    }

    /// Saves the downloads. In front, the list is handed to `manifestWriter`: written off the main thread, the newest
    /// only and at most every couple of seconds, so a playlist coming down (a save per track per step, of the whole
    /// list) doesn't hitch the screen each time. It's written out on leaving the front (see `init`). Anywhere else
    /// (woken in the background for a finished transfer, say) it's written before going on, as the app may be
    /// suspended straight after; `throttled` (a transfer's progress) then at most every two seconds. `rightAway` is
    /// for a file arriving or going: written at once even in front (still off the main thread), so the list on disk is
    /// never a moment behind the files beside it.
    private func save(throttled: Bool = false, rightAway: Bool = false) {
        manifestWriter.save(all, rightAway: rightAway)
        if isInFront { return }
        if throttled, Date().timeIntervalSince(lastSaved) < 2 { return }
        lastSaved = Date()
        manifestWriter.flush()
    }

    private func saveCollections() {
        if let data = try? Self.encoder.encode(collections) { try? data.write(to: Self.collectionsManifest, options: .atomic) }
    }

    /// A book's file is "id#n": its download and which file.
    nonisolated fileprivate static func bookFile(_ description: String) -> (id: String, index: Int)? {
        let parts = description.split(separator: "#")
        guard parts.count == 2, let index = Int(parts[1]) else { return nil }
        return (String(parts[0]), index)
    }

    fileprivate func bookProgressed(_ id: String, file: Int, _ fraction: Double) {
        guard let index = all.firstIndex(where: { $0.id == id }), let book = all[index].book else { return }
        bookFileProgress[id, default: [:]][file] = fraction
        let sizes = book.manifest.files.map { Double(max(1, $0.size)) }
        let total = sizes.reduce(0, +)
        let done = sizes.indices.reduce(0.0) { sum, n in sum + sizes[n] * (book.files[n] != nil ? 1 : bookFileProgress[id]?[n] ?? 0) }
        let whole = min(0.999, done / total)
        guard shows(whole, of: id, after: all[index].state) else { return }
        all[index].state = .downloading(whole)
        save(throttled: true)
    }

    /// The folder a book's files go in, and the name file `n` is kept under (with the extension it came as).
    nonisolated fileprivate static func bookFileName(_ index: Int, sent: String?, file: BookFile?) -> String {
        let fallback = file?.converted == true ? "m4a" : (file?.container?.split(separator: ",").first.map(String.init) ?? "m4a")
        let ext = [sent ?? "", fallback].first { !$0.isEmpty && $0.count <= 5 } ?? "m4a"
        return String(format: "%03d.%@", index, ext == "mp4" ? "m4a" : ext)
    }

    fileprivate func bookFileArrived(_ id: String, file: Int, name: String) {
        guard let download = all.first(where: { $0.id == id }), var book = download.book, book.files.indices.contains(file) else { return }
        book.files[file] = name
        let complete = book.isComplete
        update(id) {
            $0.book = book
            if complete { $0.state = .downloaded }
        }
        save(rightAway: true) // a file is here
        if complete {
            bookFileProgress[id] = nil
            progressShownAt[id] = nil
            log.info("Downloaded the book \(download.item.title)")
        } else {
            bookProgressed(id, file: file, 1)
        }
    }

    /// A household device's transfer to the home address cut off, or unable to connect there (it's left home): the
    /// requests move to the tunnel (started for them) unless home answers after all, and the transfer goes again at the
    /// address in use, from where it got to only if that's the same address (see `startTransfer`). True when that's
    /// what happened.
    private func lostHome(_ error: Error, url: URL?) -> Bool {
        guard let client, let url, client.addresses.hasHomeRoute, client.addresses.isHome(url),
              (error as NSError).userInfo[NSURLSessionDownloadTaskResumeData] != nil || FlexarrClient.couldNotConnect(error)
        else { return false }
        log.info("A download lost the home address: carrying on through the tunnel")
        let addresses = client.addresses
        Task { @MainActor in
            if !(await client.homeAnswers(fresh: true)), await FlexarrClient.onTunnelNeeded?() == true, let tunnel = addresses.tunnel {
                addresses.reached(tunnel)
            }
            if isOnline { poll() }
        }
        return true
    }

    fileprivate func bookFileStopped(_ id: String, file: Int, _ error: Error, wifiOnly: Bool, url: URL?) {
        guard let download = all.first(where: { $0.id == id }), !download.isDownloaded else { return }
        if Self.isOutOfSpace(error) {
            update(id) { $0.state = .failed(Self.outOfSpaceMessage) }
            return
        }
        // Cut off part-way (or cancelled by the system with where it got to, the app swiped away): carried on from
        // there at the next catch-up. One that lost the home address carries on through the tunnel.
        let data = (error as NSError).userInfo[NSURLSessionDownloadTaskResumeData] as? Data
        if let data { resumeData["\(id)#\(file)"] = Resumable(data: data, wifiOnly: wifiOnly, url: url) }
        if lostHome(error, url: url) || data != nil { return }
        if (error as? URLError)?.code == .cancelled { return }
        update(id) { $0.state = .failed(t("The download stopped. Try again.")) }
    }

    fileprivate func progressed(_ id: String, _ fraction: Double) {
        if let (book, file) = Self.bookFile(id) { return bookProgressed(book, file: file, fraction) }
        guard let index = all.firstIndex(where: { $0.id == id }), shows(fraction, of: id, after: all[index].state) else { return }
        all[index].state = .downloading(fraction)
        save(throttled: true)
    }

    /// Whether a transfer's progress has moved on by a percent that shows, and not too soon after the last one shown
    /// (see `ProgressPacing`): each change to `all` redraws every screen that lists downloads. Saying yes counts as shown.
    private func shows(_ fraction: Double, of id: String, after state: LocalDownload.State) -> Bool {
        let shown: Double? = if case .downloading(let shown) = state { shown } else { nil }
        let now = Date()
        guard ProgressPacing.shows(fraction, after: shown, shownAt: progressShownAt[id], now: now) else { return false }
        progressShownAt[id] = now
        return true
    }

    fileprivate func arrived(_ id: String, fileName: String, bytes: Int64) {
        progressShownAt[id] = nil
        update(id) {
            $0.state = .downloaded
            $0.fileName = fileName
            $0.sizeBytes = bytes
        }
        save(rightAway: true) // its file is here
        log.info("Downloaded \(id)")
        guard let download = all.first(where: { $0.id == id }) else { return }
        // The server lets its copy go, told as the download's own profile: another's (it came while someone else was
        // watching), or out of reach, is told on a catch-up signed in as theirs.
        unsentReceipts.insert(ProfileTells.entry(id, profileId: download.profileId))
        if let client, attachedProfileId == download.profileId {
            Task { await tellDue(with: client) }
        }
        fetchNext(after: download)
    }

    /// `wifiOnly`: whether the transfer was made Wi-Fi only, kept with where it got to.
    /// `url`: the address it was going to, kept with where it got to.
    fileprivate func stopped(_ id: String, _ error: Error, wifiOnly: Bool = false, url: URL? = nil) {
        if let (book, file) = Self.bookFile(id) { return bookFileStopped(book, file: file, error, wifiOnly: wifiOnly, url: url) }
        guard let download = all.first(where: { $0.id == id }), !download.isDownloaded else { return }
        // The device is full: it would only stop again.
        if Self.isOutOfSpace(error) {
            update(id) { $0.state = .failed(Self.outOfSpaceMessage) }
            return
        }
        // A household device's transfer that lost the home address (it left home): it carries on through the tunnel.
        if lostHome(error, url: url) {
            held(id, resumeData: (error as NSError).userInfo[NSURLSessionDownloadTaskResumeData] as? Data, wifiOnly: wifiOnly, url: url)
            return
        }
        // Cut off part-way (the network went, a guest's tunnel slept with the phone locked, or the system cancelled it
        // when the app was swiped away): back to waiting, to carry on from where it got to once the server's in reach
        // again (the next catch-up, back in front). Checked before "cancelled", which is how the system reports a
        // transfer it stopped with the app.
        if let data = (error as NSError).userInfo[NSURLSessionDownloadTaskResumeData] as? Data {
            held(id, resumeData: data, wifiOnly: wifiOnly, url: url)
            if isOnline { poll() }
            return
        }
        if (error as? URLError)?.code == .cancelled { return }
        update(id) { $0.state = .failed(t("The download stopped. Try again.")) }
    }
}

// MARK: Space

extension DownloadStore {
    /// The space free on this device for what the person asks for (`volumeAvailableCapacityForImportantUsage`).
    nonisolated static var freeBytes: Int64? {
        (try? folder.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]))?.volumeAvailableCapacityForImportantUsage
    }

    static var outOfSpaceMessage: String {
        t("Not enough space on this {device}. Free some up and try again.", ["device": ThisDevice.name])
    }

    /// A transfer stopped, or its file couldn't be moved into place, because the device is full: the file system says
    /// so, or the downloader couldn't write with almost nothing free.
    nonisolated static func isOutOfSpace(_ error: Error) -> Bool {
        let error = error as NSError
        switch (error.domain, error.code) {
        case (NSCocoaErrorDomain, NSFileWriteOutOfSpaceError), (NSPOSIXErrorDomain, Int(ENOSPC)): return true
        case (NSURLErrorDomain, NSURLErrorCannotWriteToFile), (NSURLErrorDomain, NSURLErrorCannotCreateFile):
            if let free = freeBytes, free < 500_000_000 { return true }
        default: break
        }
        if let underlying = error.userInfo[NSUnderlyingErrorKey] as? Error { return isOutOfSpace(underlying) }
        return false
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
        if let (bookId, index) = Self.bookFile(id) {
            // One of a book's files: into the book's folder, as its number. Deleted meanwhile, it's left for the
            // system to delete, rather than making the book's folder again.
            guard MainActor.assumeIsolated({ all.contains { $0.id == bookId && $0.book != nil } }) else { return }
            let file = MainActor.assumeIsolated { all.first { $0.id == bookId }?.book?.manifest.files.first { $0.index == index } }
            let name = Self.bookFileName(index, sent: sent, file: file)
            let destination = DownloadStore.folder.appending(path: bookId, directoryHint: .isDirectory).appending(path: name)
            try? FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? FileManager.default.removeItem(at: destination)
            do {
                try FileManager.default.moveItem(at: location, to: destination)
                MainActor.assumeIsolated { bookFileArrived(bookId, file: index, name: name) }
            } catch {
                MainActor.assumeIsolated { stopped(id, error) }
            }
            return
        }
        // Deleted as it finished: nothing to keep it for.
        guard MainActor.assumeIsolated({ all.contains { $0.id == id } }) else { return }
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
        let wifiOnly = Self.madeWiFiOnly(task)
        let url = Self.address(of: task)
        MainActor.assumeIsolated { stopped(id, error, wifiOnly: wifiOnly, url: url) }
    }

    nonisolated func urlSessionDidFinishEvents(forBackgroundURLSession session: URLSession) {
        MainActor.assumeIsolated {
            // Saved before the system is told it can suspend the app again.
            manifestWriter.flush()
            backgroundCompletion?()
            backgroundCompletion = nil
        }
    }
}

/// The podcast episodes this phone was told not to keep (their downloads deleted by hand), by episode id: in the app's
/// settings and the keychain, which outlives reinstalling the app.
private enum DeclinedEpisodes {
    private static let key = "downloads.podcasts.declined"
    private static var query: [String: Any] { KeychainPlacement.placed([
        kSecClass as String: kSecClassGenericPassword,
        kSecAttrService as String: "io.github.flexarr.declined-episodes",
        kSecAttrAccount as String: "episodes",
    ]) }

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
