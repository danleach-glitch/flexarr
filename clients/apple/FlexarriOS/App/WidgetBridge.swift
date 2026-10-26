import FlexarrKit
import UIKit
import WidgetKit

/// Keeps the podcast widgets' snapshot up to date: the episode being heard (else the one to resume), whether it's
/// playing, and the next few in Up Next, each with a small picture saved beside it. Called when podcast state changes;
/// changes that come in a burst are written once.
@MainActor
enum WidgetBridge {
    private static var pending: Task<Void, Never>?
    /// The episode to resume when nothing's playing, as last fetched.
    private static var resume: PodcastEpisode?
    private static var resumeFetchedAt = Date.distantPast

    static func publish(_ model: AppModel) {
        pending?.cancel()
        pending = Task {
            try? await Task.sleep(for: .milliseconds(800))
            guard !Task.isCancelled else { return }
            await write(model)
        }
    }

    private static func write(_ model: AppModel) async {
        guard PodcastSnapshot.folder != nil, let client = model.client else { return }
        let music = model.music
        var current: PodcastSnapshot.Episode?
        if music.isPodcast, let track = music.current {
            let duration = music.duration > 0 ? music.duration : track.durationSeconds ?? 0
            current = await episode(id: track.id, title: track.title, show: track.seriesTitle ?? "", artwork: track.artwork, client: client,
                                    progress: duration > 0 ? music.position / duration : nil,
                                    remaining: duration > 0 ? duration - music.position : nil)
        } else {
            if Date().timeIntervalSince(resumeFetchedAt) > 300 {
                resume = try? await client.continueListening(take: 1).first
                resumeFetchedAt = Date()
            }
            if let resume {
                current = await episode(id: resume.id, title: resume.title, show: resume.podcastTitle, artwork: resume.artwork, client: client,
                                        progress: resume.progress, remaining: resume.durationSeconds.map { $0 - resume.userData.positionSeconds })
            }
        }
        var next: [PodcastSnapshot.Episode] = []
        for queued in model.upNext.filter({ $0.id != current?.id }).prefix(3) {
            next.append(await episode(id: queued.id, title: queued.title, show: queued.podcastTitle, artwork: queued.artwork, client: client,
                                      progress: queued.progress, remaining: queued.durationSeconds))
        }
        let snapshot = PodcastSnapshot(current: current, isPlaying: music.isPodcast && music.isPlaying, upNext: next, updatedAt: Date())
        let previous = PodcastSnapshot.load()
        snapshot.save()
        removeUnusedArtwork(keeping: snapshot)
        // Progress alone moving isn't worth spending the widgets' refresh budget on.
        if previous.current?.id != snapshot.current?.id || previous.isPlaying != snapshot.isPlaying || previous.upNext != snapshot.upNext
            || abs((previous.current?.progress ?? 0) - (snapshot.current?.progress ?? 0)) > 0.05 {
            WidgetCenter.shared.reloadAllTimelines()
        }
    }

    private static func episode(id: String, title: String, show: String, artwork: Artwork, client: FlexarrClient,
                                progress: Double?, remaining: Double?) async -> PodcastSnapshot.Episode {
        PodcastSnapshot.Episode(id: id, title: title, show: show, artworkFile: await savedArtwork(for: id, artwork: artwork, client: client),
                                progress: progress, remainingSeconds: remaining)
    }

    /// A small square picture for the widget, saved once per episode.
    private static func savedArtwork(for id: String, artwork: Artwork, client: FlexarrClient) async -> String? {
        let name = "art-\(id).jpg"
        guard let url = PodcastSnapshot.artwork(name) else { return nil }
        if FileManager.default.fileExists(atPath: url.path(percentEncoded: false)) { return name }
        guard let source = client.imageURL(artwork.thumb ?? artwork.poster, width: 342), let image = await ImageLoader.shared.load(source),
              let data = image.jpegData(compressionQuality: 0.8) else { return nil }
        try? data.write(to: url, options: .atomic)
        return name
    }

    private static func removeUnusedArtwork(keeping snapshot: PodcastSnapshot) {
        guard let folder = PodcastSnapshot.folder, let files = try? FileManager.default.contentsOfDirectory(atPath: folder.path(percentEncoded: false)) else { return }
        let used = Set(([snapshot.current] + snapshot.upNext.map(Optional.some)).compactMap { $0?.artworkFile })
        for file in files where file.hasPrefix("art-") && !used.contains(file) {
            try? FileManager.default.removeItem(at: folder.appending(path: file))
        }
    }
}
