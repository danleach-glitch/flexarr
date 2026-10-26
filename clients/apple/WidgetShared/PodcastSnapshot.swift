import Foundation

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

    /// The link a widget opens: plays (or resumes) the episode in the app.
    static func playURL(_ episodeId: String) -> URL {
        URL(string: "flexarr://play/\(episodeId)") ?? URL(string: "flexarr://")!
    }
}
