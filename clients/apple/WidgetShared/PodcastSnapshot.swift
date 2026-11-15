import Foundation
import FlexarrKit

/// What the podcast widgets show, as the iPhone app last saw it: the episode being heard (or the one to resume) and
/// the next few in Up Next. The app writes it to the app group whenever podcast state changes; widgets only read it,
/// and never call the server.
struct PodcastSnapshot: Codable, Equatable {
    struct Episode: Codable, Equatable, Identifiable {
        let id: String
        let title: String
        let show: String
        /// A small square picture in the app group's folder, when one was saved.
        let artworkFile: String?
        /// 0 to 1, or nil when it hasn't been started.
        let progress: Double?
        let remainingSeconds: Double?
    }

    var current: Episode?
    var isPlaying: Bool
    var upNext: [Episode]
    var updatedAt: Date

    static let empty = PodcastSnapshot(current: nil, isPlaying: false, upNext: [], updatedAt: .distantPast)

    /// The app group both the app and the widgets belong to, from their Info.plist.
    static var groupId: String? { Bundle.main.object(forInfoDictionaryKey: "FlexarrAppGroup") as? String }

    static var folder: URL? {
        groupId.flatMap { FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: $0) }?
            .appending(path: "Widgets", directoryHint: .isDirectory)
    }

    private static var file: URL? { folder?.appending(path: "podcasts.json") }

    static func load() -> PodcastSnapshot {
        guard let file, let data = try? Data(contentsOf: file),
              let snapshot = try? JSONDecoder().decode(PodcastSnapshot.self, from: data) else { return .empty }
        return snapshot
    }

    func save() {
        guard let folder = Self.folder, let file = Self.file else { return }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        if let data = try? JSONEncoder().encode(self) { try? data.write(to: file, options: .atomic) }
    }

    /// Where an episode's saved picture is.
    static func artwork(_ name: String?) -> URL? {
        guard let name else { return nil }
        return folder?.appending(path: name)
    }

    /// The link a widget opens: plays (or resumes) the episode in the app. It carries the app's link key (see
    /// `OwnLinks`), which the app leaves in the app group for the widgets, so the app knows it's its own.
    static func playURL(_ episodeId: String) -> URL {
        let url = URL(string: "flexarr://play/\(episodeId)") ?? URL(string: "flexarr://")!
        return linkKey.map { OwnLinks.signed(url, key: $0) } ?? url
    }

    private static var linkKeyFile: URL? { folder?.appending(path: "link-key") }

    /// The app's link key, as it last left it for the widgets.
    static var linkKey: String? {
        linkKeyFile.flatMap { try? String(contentsOf: $0, encoding: .utf8) }.flatMap { $0.isEmpty ? nil : $0 }
    }

    /// Leaves the app's link key where the widgets can read it (the app group's folder, which only Flexarr can open).
    static func saveLinkKey(_ key: String) {
        guard let folder, let file = linkKeyFile, linkKey != key else { return }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? Data(key.utf8).write(to: file, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
    }
}
