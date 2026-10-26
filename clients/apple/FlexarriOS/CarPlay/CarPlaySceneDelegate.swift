import CarPlay
import FlexarrKit
import UIKit

/// Podcasts in the car: tabs for Up Next, New episodes and Podcasts (the shows followed, each opening its episodes),
/// lists with artwork, and the system's Now Playing screen with skip back 15, forward 30 (the player's remote commands)
/// and a speed button. Everything plays through the app's own player, so the phone and the car agree.
@MainActor
final class CarPlaySceneDelegate: UIResponder, CPTemplateApplicationSceneDelegate {
    /// The app model, handed over at launch (the car's scene has no SwiftUI environment).
    static weak var model: AppModel?

    private var interfaceController: CPInterfaceController?
    private let upNext = CPListTemplate(title: "Up Next", sections: [])
    private let fresh = CPListTemplate(title: "New episodes", sections: [])
    private let podcasts = CPListTemplate(title: "Podcasts", sections: [])
    /// Kept here: the Now Playing template only holds its observers weakly.
    private var upNextObserver: UpNextObserver?

    func templateApplicationScene(_ templateApplicationScene: CPTemplateApplicationScene, didConnect interfaceController: CPInterfaceController) {
        self.interfaceController = interfaceController
        upNext.tabImage = UIImage(systemName: "list.bullet")
        fresh.tabImage = UIImage(systemName: "sparkles")
        podcasts.tabImage = UIImage(systemName: "mic.fill")
        for list in [upNext, fresh, podcasts] { list.emptyViewSubtitleVariants = ["Nothing here yet."] }
        interfaceController.setRootTemplate(CPTabBarTemplate(templates: [upNext, fresh, podcasts]), animated: false, completion: nil)
        setUpNowPlaying()
        Task { await reload() }
    }

    func templateApplicationScene(_ templateApplicationScene: CPTemplateApplicationScene, didDisconnectInterfaceController interfaceController: CPInterfaceController) {
        self.interfaceController = nil
    }

    // MARK: Lists

    private func reload() async {
        guard let model = Self.model, let client = model.client else {
            for list in [upNext, fresh, podcasts] { list.emptyViewSubtitleVariants = ["Open Flexarr on your iPhone to sign in."] }
            return
        }
        await model.loadUpNext()
        upNext.updateSections([CPListSection(items: await items(model.upNext, client: client))])
        let new = (try? await client.newPodcastEpisodes(unplayed: true, take: 25)) ?? []
        fresh.updateSections([CPListSection(items: await items(new, client: client))])
        let shows = (try? await client.podcasts()) ?? []
        var rows: [CPListItem] = []
        for show in shows {
            let row = CPListItem(text: show.title, detailText: show.countLine, image: await artwork(show.artwork.poster, client: client))
            row.accessoryType = .disclosureIndicator
            row.handler = { [weak self] _, completion in
                Task { @MainActor in
                    await self?.open(show, client: client)
                    completion()
                }
            }
            rows.append(row)
        }
        podcasts.updateSections([CPListSection(items: rows)])
    }

    /// A show's unplayed episodes, newest first (or oldest, as it's listened to).
    private func open(_ show: Podcast, client: FlexarrClient) async {
        let oldest = show.subscription?.oldestFirst == true
        let page = (try? await client.podcastEpisodes(show.id, sort: oldest ? .oldest : .newest, unplayed: true, take: 40))?.items ?? []
        let list = CPListTemplate(title: show.title, sections: [CPListSection(items: await items(page, client: client, showsPodcast: false))])
        list.emptyViewSubtitleVariants = ["You're up to date."]
        interfaceController?.pushTemplate(list, animated: true, completion: nil)
    }

    private func items(_ episodes: [PodcastEpisode], client: FlexarrClient, showsPodcast: Bool = true) async -> [CPListItem] {
        var rows: [CPListItem] = []
        for episode in episodes {
            let detail = [showsPodcast ? episode.podcastTitle : nil, episode.timeLine].compactMap { $0 }.joined(separator: "  ")
            let row = CPListItem(text: episode.title, detailText: detail,
                                 image: await artwork(episode.artwork.thumb ?? episode.artwork.poster, client: client))
            row.isPlaying = Self.model?.music.current?.id == episode.id
            row.playbackProgress = CGFloat(episode.progress ?? 0)
            row.handler = { [weak self] _, completion in
                Task { @MainActor in
                    Self.model?.playEpisode(episode.summary)
                    self?.showNowPlaying()
                    completion()
                }
            }
            rows.append(row)
        }
        return rows
    }

    private func artwork(_ path: String?, client: FlexarrClient) async -> UIImage? {
        guard let url = client.imageURL(path, width: 185) else { return nil }
        return await ImageLoader.shared.load(url)
    }

    // MARK: Now Playing

    private func setUpNowPlaying() {
        let speed = CPNowPlayingPlaybackRateButton { _ in
            Task { @MainActor in
                guard let music = Self.model?.music, music.isPodcast else { return }
                // Round the speeds a podcast app offers.
                let speeds = MusicPlayer.speeds
                let next = speeds.first { $0 > music.speed + 0.01 } ?? speeds[0]
                music.setSpeed(next)
            }
        }
        let nowPlaying = CPNowPlayingTemplate.shared
        nowPlaying.updateNowPlayingButtons([speed])
        nowPlaying.isUpNextButtonEnabled = true
        nowPlaying.upNextTitle = "Up Next"
        let observer = UpNextObserver(open: { [weak self] in
            guard let self else { return }
            interfaceController?.pushTemplate(upNext, animated: true, completion: nil)
        })
        upNextObserver = observer
        nowPlaying.add(observer)
    }

    private func showNowPlaying() {
        guard let interfaceController, interfaceController.topTemplate !== CPNowPlayingTemplate.shared else { return }
        interfaceController.pushTemplate(CPNowPlayingTemplate.shared, animated: true, completion: nil)
        Task {
            try? await Task.sleep(for: .seconds(2))
            await reload() // Up Next drops what just started
        }
    }
}

/// Up Next, from the Now Playing screen's button (which CarPlay taps on the main thread).
private final class UpNextObserver: NSObject, CPNowPlayingTemplateObserver, @unchecked Sendable {
    let open: @MainActor @Sendable () -> Void

    init(open: @escaping @MainActor @Sendable () -> Void) {
        self.open = open
    }

    func nowPlayingTemplateUpNextButtonTapped(_ nowPlayingTemplate: CPNowPlayingTemplate) {
        let open = self.open
        MainActor.assumeIsolated { open() }
    }
}
