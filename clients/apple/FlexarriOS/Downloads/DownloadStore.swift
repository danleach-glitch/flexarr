import FlexarrKit
import Foundation
import Network
import Observation
import os
import UIKit

private let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "io.github.flexarr", category: "downloads")

/// A film or episode kept on this device for watching with no connection.
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
    var imageFileName: String?
    /// Where watching got to, and when it was finished (it's deleted a day after).
    var position: Double = 0
    var finishedAt: Date?
    /// Watched offline and not yet told to the server.
    var unsent: UnsentProgress?
    /// Fetched by itself, as the next episode.
    var automatic = false
    var createdAt = Date()

    var isDownloaded: Bool { state == .downloaded && fileName != nil }
}

struct UnsentProgress: Codable, Hashable {
    var position: Double
    var finished: Bool
    var playedAt: Date
}

/// Downloads on this device: asking the server to get them ready, fetching them in the background (even with the
/// app closed), playing them offline, sending what was watched once there's a connection, fetching the next
/// episode by itself (on Wi-Fi), and deleting episodes a day after they're finished.
@MainActor
@Observable
final class DownloadStore: NSObject, OfflineLibrary {
    static let sessionIdentifier = "io.github.flexarr.downloads"

    private(set) var all: [LocalDownload] = []
    /// The profile watching: whose downloads show. Remembered, so they show with no connection too.
    private(set) var profileId: String? = UserDefaults.standard.string(forKey: "downloads.profile")
    /// Fetch the next episode by itself, on Wi-Fi.
    var downloadsNext: Bool = UserDefaults.standard.object(forKey: "downloads.next") as? Bool ?? true {
        didSet { UserDefaults.standard.set(downloadsNext, forKey: "downloads.next") }
    }
    private(set) var isOnline = true
    private(set) var isOnWiFi = false

    /// Called when the system has finished delivering background downloads (see the app delegate).
    @ObservationIgnored var backgroundCompletion: (() -> Void)?

    @ObservationIgnored private var client: FlexarrClient?
    @ObservationIgnored private var session: URLSession!
    @ObservationIgnored private var polling: Task<Void, Never>?
    @ObservationIgnored private let monitor = NWPathMonitor()
    @ObservationIgnored private var lastSaved = Date.distantPast

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

    override init() {
        super.init()
        if let data = try? Data(contentsOf: Self.manifest), let saved = try? FlexarrClient.decoder.decode([LocalDownload].self, from: data) {
            all = saved
        }
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

    /// This profile's downloads, newest first.
    var downloads: [LocalDownload] {
        all.filter { $0.profileId == profileId }.sorted { $0.createdAt > $1.createdAt }
    }

    func entry(for itemId: String) -> LocalDownload? {
        all.first { $0.profileId == profileId && $0.item.id == itemId }
    }

    var bytesUsed: Int64 {
        all.filter(\.isDownloaded).compactMap(\.sizeBytes).reduce(0, +)
    }

    // MARK: Connecting

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
        isOnline = online
        isOnWiFi = wifi
        if cameBack { Task { await catchUp() } }
    }

    /// Sends what was watched offline, follows what the server is getting ready, deletes what's finished with, and
    /// fetches next episodes that are due.
    func catchUp() async {
        deleteFinished()
        guard let client, isOnline else { return }
        for download in all where download.profileId == profileId {
            guard let unsent = download.unsent else { continue }
            do {
                try await client.reportOfflineProgress(itemId: download.item.id, positionSeconds: unsent.position, finished: unsent.finished, playedAt: unsent.playedAt)
                update(download.id) { if $0.unsent == unsent { $0.unsent = nil } }
            } catch {
                log.error("Couldn't send offline progress: \(error.localizedDescription, privacy: .public)")
            }
        }
        for download in downloads where download.isDownloaded && download.finishedAt == nil {
            fetchNext(after: download)
        }
        poll()
    }

    // MARK: Downloading

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
                                  sizeBytes: remote.sizeBytes, automatic: automatic)
        all.removeAll { $0.id == remote.id }
        all.append(local)
        save()
        log.info("Downloading \(remote.item.title, privacy: .public) at \(quality.rawValue, privacy: .public)")
        if let image = await fetchImage(for: remote.item, id: remote.id) {
            local.imageFileName = image
            update(remote.id) { $0.imageFileName = image }
        }
        apply(remote)
        poll()
    }

    /// Follows the server getting downloads ready, every few seconds while any are.
    private func poll() {
        guard polling == nil else { return }
        polling = Task { [weak self] in
            while let self, !Task.isCancelled {
                let waiting = all.filter { if case .preparing = $0.state { return true } else { return false } }
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
            update(remote.id) {
                $0.state = .downloading(0)
                $0.sizeBytes = remote.sizeBytes
                $0.nextId = remote.nextId
                $0.markers = remote.markers
            }
            let task = session.downloadTask(with: client.downloadFileRequest(remote.id))
            task.taskDescription = remote.id
            task.resume()
        case .received where !local.isDownloaded, .expired:
            update(remote.id) { $0.state = .failed("This download ran out on the server. Download it again.") }
        case .failed:
            update(remote.id) { $0.state = .failed(remote.error ?? "The server couldn't get this ready.") }
        default:
            break
        }
    }

    private func fetchImage(for item: ItemSummary, id: String) async -> String? {
        let path = item.kind == .episode ? (item.artwork.thumb ?? item.artwork.backdrop) : (item.artwork.backdrop ?? item.artwork.poster)
        guard let url = client?.imageURL(path, width: 500), let (data, _) = try? await URLSession.shared.data(from: url), UIImage(data: data) != nil else {
            return nil
        }
        let name = "\(id).jpg"
        try? data.write(to: Self.folder.appending(path: name))
        return name
    }

    // MARK: Deleting

    func delete(_ id: String) {
        guard let download = all.first(where: { $0.id == id }) else { return }
        session.getAllTasks { tasks in
            for task in tasks where task.taskDescription == id { task.cancel() }
        }
        for name in [download.fileName, download.imageFileName].compactMap({ $0 }) {
            try? FileManager.default.removeItem(at: Self.folder.appending(path: name))
        }
        all.removeAll { $0.id == id }
        save()
        if let client { Task { try? await client.deleteDownload(id) } }
    }

    /// Signing out: every download goes, whoever's it was.
    func deleteAll() {
        for id in all.map(\.id) { delete(id) }
        profileId = nil
        UserDefaults.standard.removeObject(forKey: "downloads.profile")
    }

    /// Episodes finished more than a day ago (films are kept until deleted).
    private func deleteFinished() {
        let dayAgo = Date().addingTimeInterval(-24 * 60 * 60)
        for download in all where download.item.kind == .episode && download.unsent == nil {
            if let finished = download.finishedAt, finished < dayAgo { delete(download.id) }
        }
    }

    // MARK: Next episode

    /// Keeps one episode ahead: when one's downloaded or watched, fetch the next, on Wi-Fi, at the same size.
    private func fetchNext(after download: LocalDownload) {
        guard downloadsNext, isOnWiFi, download.item.kind == .episode, let nextId = download.nextId, entry(for: nextId) == nil else { return }
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

    func nextInfo(after info: PlaybackInfo) -> PlaybackInfo? {
        guard let next = info.next, let download = entry(for: next.id), download.isDownloaded else { return nil }
        return playbackInfo(for: download, preferences: info.preferences)
    }

    // MARK: Saving

    private func update(_ id: String, _ change: (inout LocalDownload) -> Void) {
        guard let index = all.firstIndex(where: { $0.id == id }) else { return }
        change(&all[index])
        save()
    }

    private func save(throttled: Bool = false) {
        if throttled, Date().timeIntervalSince(lastSaved) < 2 { return }
        lastSaved = Date()
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        if let data = try? encoder.encode(all) { try? data.write(to: Self.manifest, options: .atomic) }
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
        log.info("Downloaded \(id, privacy: .public)")
        if let client { Task { try? await client.markDownloadReceived(id) } }
        if let download = all.first(where: { $0.id == id }) { fetchNext(after: download) }
    }

    fileprivate func stopped(_ id: String, _ error: Error) {
        guard let download = all.first(where: { $0.id == id }), !download.isDownloaded else { return }
        if (error as? URLError)?.code == .cancelled { return }
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
        let name = "\(id).mp4"
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
