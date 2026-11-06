import CarPlay
import FlexarrKit
import UIKit

/// Podcasts in the car: tabs for Up Next, New episodes and Podcasts (the shows followed, each opening its episodes),
/// lists with artwork, and the system's Now Playing screen with skip back 15, forward 30 (the player's remote commands)
/// and a speed button. Everything plays through the app's own player, so the phone and the car agree.
///
/// Audiobooks get one tab of their own (CarPlay allows four for audio apps): Continue listening, the books downloaded
/// on the phone, then each audiobooks library, which opens its books. A book plays where it was left, with the
/// listener's skips and chapters on the Now Playing screen.
@MainActor
final class CarPlaySceneDelegate: UIResponder, CPTemplateApplicationSceneDelegate {
    /// The app model, handed over at launch (the car's scene has no SwiftUI environment).
    static weak var model: AppModel?

    private var interfaceController: CPInterfaceController?
    private let upNext = CPListTemplate(title: t(Words.upNext), sections: [])
    private let fresh = CPListTemplate(title: t("New episodes"), sections: [])
    private let podcasts = CPListTemplate(title: t(Words.podcasts), sections: [])
    private let books = CPListTemplate(title: t("Books"), sections: [])
    /// Where downloaded books are listed from (handed over at launch, like the model).
    static weak var downloads: DownloadStore?
    /// Kept here: the Now Playing template only holds its observers weakly.
    private var upNextObserver: UpNextObserver?

    func templateApplicationScene(_ templateApplicationScene: CPTemplateApplicationScene, didConnect interfaceController: CPInterfaceController) {
        self.interfaceController = interfaceController
        upNext.tabImage = UIImage(systemName: "list.bullet")
        fresh.tabImage = UIImage(systemName: "sparkles")
        podcasts.tabImage = UIImage(systemName: "mic.fill")
        books.tabImage = UIImage(systemName: "book.fill")
        for list in [upNext, fresh, podcasts] { list.emptyViewSubtitleVariants = [t("Nothing here yet.")] }
        books.emptyViewSubtitleVariants = [t("No books yet.")]
        interfaceController.setRootTemplate(CPTabBarTemplate(templates: [upNext, fresh, podcasts, books]), animated: false, completion: nil)
        setUpNowPlaying()
        Task { await reload() }
    }

    func templateApplicationScene(_ templateApplicationScene: CPTemplateApplicationScene, didDisconnectInterfaceController interfaceController: CPInterfaceController) {
        self.interfaceController = nil
    }

    // MARK: Lists

    private func reload() async {
        guard let model = Self.model, let client = model.client else {
            for list in [upNext, fresh, podcasts, books] { list.emptyViewSubtitleVariants = [t("Open Flexarr on your iPhone to sign in.")] }
            await reloadBooks(client: nil)
            return
        }
        // A kids profile has no podcasts, nor books.
        guard !model.isKids else {
            for list in [upNext, fresh, podcasts, books] {
                list.emptyViewSubtitleVariants = [t("Not on kids profiles.")]
                list.updateSections([])
            }
            return
        }
        // The four tabs side by side, each shown as soon as the server has answered for it.
        async let upNextShown: Void = reloadUpNext(client: client)
        async let freshShown: Void = reloadFresh(client: client)
        async let podcastsShown: Void = reloadPodcasts(client: client)
        async let booksShown: Void = reloadBooks(client: client)
        _ = await (upNextShown, freshShown, podcastsShown, booksShown)
    }

    private func reloadUpNext(client: FlexarrClient) async {
        guard let model = Self.model else { return }
        await model.loadUpNext()
        upNext.updateSections([CPListSection(items: items(model.upNext, client: client))])
    }

    private func reloadFresh(client: FlexarrClient) async {
        let new = (try? await client.newPodcastEpisodes(unplayed: true, take: 25)) ?? []
        fresh.updateSections([CPListSection(items: items(new, client: client))])
    }

    private func reloadPodcasts(client: FlexarrClient) async {
        let shows = (try? await client.podcasts()) ?? []
        var rows: [CPListItem] = []
        for show in shows {
            let row = CPListItem(text: show.title, detailText: show.countLine, image: nil)
            setArtwork(show.artwork.poster, on: row, client: client)
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

    // MARK: Books

    /// Continue listening, the downloaded books, and each audiobooks library. With no connection, the downloads alone.
    private func reloadBooks(client: FlexarrClient?) async {
        var sections: [CPListSection] = []
        let libraries = Self.model?.libraries.filter { $0.type == .audiobooks } ?? []
        if let client, let library = libraries.first, let shelves = try? await client.bookShelves(library.id), !shelves.continueListening.isEmpty {
            sections.append(CPListSection(items: bookItems(shelves.continueListening, client: client), header: t("Continue listening"), sectionIndexTitle: nil))
        }
        let downloaded = (Self.downloads?.bookDownloads ?? []).filter(\.isDownloaded)
        if !downloaded.isEmpty {
            var rows: [CPListItem] = []
            for download in downloaded {
                let row = CPListItem(text: download.item.title, detailText: download.item.bookAuthors,
                                     image: Self.downloads?.image(named: download.imageFileName))
                row.playbackProgress = download.durationSeconds > 0 && download.finishedAt == nil ? CGFloat(min(1, download.position / download.durationSeconds)) : 0
                row.isPlaying = Self.model?.music.current?.id == download.item.id
                let book = download.item
                row.handler = { [weak self] _, completion in
                    Task { @MainActor in
                        self?.play(book)
                        completion()
                    }
                }
                rows.append(row)
            }
            sections.append(CPListSection(items: rows, header: t("Downloaded"), sectionIndexTitle: nil))
        }
        if let client, !libraries.isEmpty {
            let rows = libraries.map { library in
                let row = CPListItem(text: library.name, detailText: nil, image: UIImage(systemName: "book.fill"))
                row.accessoryType = .disclosureIndicator
                row.handler = { [weak self] _, completion in
                    Task { @MainActor in
                        await self?.open(library, client: client)
                        completion()
                    }
                }
                return row
            }
            sections.append(CPListSection(items: rows, header: t("Library"), sectionIndexTitle: nil))
        }
        books.updateSections(sections)
    }

    /// An audiobooks library's books, the most recently listened first.
    private func open(_ library: Library, client: FlexarrClient) async {
        let page = (try? await client.books(library.id, filter: BookFilter(sort: .listened), take: 100))?.items ?? []
        let list = CPListTemplate(title: library.name, sections: [CPListSection(items: bookItems(page, client: client))])
        list.emptyViewSubtitleVariants = [t("No books yet.")]
        interfaceController?.pushTemplate(list, animated: true, completion: nil)
    }

    private func bookItems(_ list: [ItemSummary], client: FlexarrClient) -> [CPListItem] {
        var rows: [CPListItem] = []
        for book in list {
            let row = CPListItem(text: book.title, detailText: book.bookAuthors, image: nil)
            setArtwork(book.artwork.poster, on: row, client: client)
            row.isPlaying = Self.model?.music.current?.id == book.id
            row.playbackProgress = CGFloat(book.progress ?? 0)
            row.handler = { [weak self] _, completion in
                Task { @MainActor in
                    self?.play(book)
                    completion()
                }
            }
            rows.append(row)
        }
        return rows
    }

    private func play(_ book: ItemSummary) {
        Self.model?.playBook(book)
        showNowPlaying()
    }

    /// A show's unplayed episodes, newest first (or oldest, as it's listened to).
    private func open(_ show: Podcast, client: FlexarrClient) async {
        let oldest = show.subscription?.oldestFirst == true
        let page = (try? await client.podcastEpisodes(show.id, sort: oldest ? .oldest : .newest, unplayed: true, take: 40))?.items ?? []
        let list = CPListTemplate(title: show.title, sections: [CPListSection(items: items(page, client: client, showsPodcast: false))])
        list.emptyViewSubtitleVariants = [t("You’re up to date.")]
        interfaceController?.pushTemplate(list, animated: true, completion: nil)
    }

    private func items(_ episodes: [PodcastEpisode], client: FlexarrClient, showsPodcast: Bool = true) -> [CPListItem] {
        var rows: [CPListItem] = []
        for episode in episodes {
            let detail = [showsPodcast ? episode.podcastTitle : nil, episode.timeLine].compactMap { $0 }.joined(separator: "  ")
            let row = CPListItem(text: episode.title, detailText: detail, image: nil)
            setArtwork(episode.artwork.thumb ?? episode.artwork.poster, on: row, client: client)
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

    /// A row's cover: there at once when it's been shown before, otherwise put on the row when it arrives. So a list
    /// goes up with its words straight away, and its covers come together, rather than the list staying empty until
    /// every cover has been fetched in turn (tens of seconds on mobile data).
    private func setArtwork(_ path: String?, on row: CPListItem, client: FlexarrClient) {
        guard let url = client.imageURL(path, width: 185) else { return }
        if let cached = ImageCache.shared.image(for: url) {
            row.setImage(cached)
            return
        }
        row.setImage(Self.artworkPlaceholder)
        Task { row.setImage(await ImageLoader.shared.load(url)) }
    }

    /// Holds a cover's place (with nothing drawn) until it arrives, so the row's words don't jump across when it does.
    private static let artworkPlaceholder = UIGraphicsImageRenderer(size: CPListItem.maximumImageSize).image { _ in }

    // MARK: Now Playing

    private func setUpNowPlaying() {
        let speed = CPNowPlayingPlaybackRateButton { _ in
            Task { @MainActor in
                guard let music = Self.model?.music, music.isPodcast || music.isBook else { return }
                // Round the speeds a podcast app offers (a book's own list).
                let speeds = music.isBook ? MusicPlayer.bookLockScreenSpeeds : MusicPlayer.speeds
                let next = speeds.first { $0 > music.speed + 0.01 } ?? speeds[0]
                music.setSpeed(next)
            }
        }
        let nowPlaying = CPNowPlayingTemplate.shared
        nowPlaying.updateNowPlayingButtons([speed])
        nowPlaying.isUpNextButtonEnabled = true
        nowPlaying.upNextTitle = t(Words.upNext)
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
