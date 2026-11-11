import FlexarrKit
import SwiftUI

/// Decoded-image memory cache. Disk caching is left to URLCache (configured at launch). A picture from the server is
/// kept under its home address whichever address it came from (`ServerAddresses.atHome`), so what was loaded at home
/// is still there, and still on screen, when the app moves to the server's address for away from home.
final class ImageCache: @unchecked Sendable {
    static let shared = ImageCache()
    private let cache = NSCache<NSURL, PlatformImage>()

    init() {
        // An Apple TV has less memory to spare than a phone, and its grids are large: decoded artwork adds up.
        #if os(tvOS)
        cache.totalCostLimit = 150 * 1024 * 1024
        #else
        cache.totalCostLimit = 300 * 1024 * 1024
        #endif
    }

    func image(for url: URL) -> PlatformImage? {
        cache.object(forKey: ServerAddresses.atHome(url) as NSURL)
    }

    func insert(_ image: PlatformImage, for url: URL) {
        let pixels = image.pixelSize
        let cost = Int(pixels.width * pixels.height * 4)
        cache.setObject(image, forKey: ServerAddresses.atHome(url) as NSURL, cost: cost)
    }
}

/// Loads and decodes images off the main thread, coalescing duplicate requests.
actor ImageLoader {
    static let shared = ImageLoader()
    private struct Load {
        let task: Task<PlatformImage?, Never>
        var waiters: Int
    }
    private var inFlight: [URL: Load] = [:]

    /// Cancelling the caller (a poster scrolled away before it arrived) stops the download,
    /// unless another view is still waiting for the same image.
    func load(_ url: URL) async -> PlatformImage? {
        if let cached = ImageCache.shared.image(for: url) { return cached }
        let task: Task<PlatformImage?, Never>
        if var load = inFlight[url] {
            load.waiters += 1
            inFlight[url] = load
            task = load.task
        } else {
            task = Task<PlatformImage?, Never> {
                guard let data = await Self.fetch(url), let image = PlatformImage(data: data) else { return nil }
                let decoded = await image.preparedForDisplay()
                ImageCache.shared.insert(decoded, for: url)
                return decoded
            }
            inFlight[url] = Load(task: task, waiters: 1)
        }
        let image = await withTaskCancellationHandler {
            await task.value
        } onCancel: {
            Task { await self.abandon(url, task) }
        }
        if inFlight[url]?.task == task { inFlight[url] = nil }
        return image
    }

    /// The image's bytes. A server too busy to make it right now (503, after a fast scroll through uncached posters)
    /// is asked again after its Retry-After, twice at most, for as long as someone is still waiting for it.
    ///
    /// Away from home, a picture the disk cache has from home is used rather than downloaded again, and one fetched
    /// now is kept where a request at home will find it (`KeptArtwork`): once, not a copy for each address.
    private static func fetch(_ url: URL) async -> Data? {
        let home = ServerAddresses.atHome(url)
        let away = home != url
        if away, let kept = KeptArtwork.data(for: home) { return kept }
        let session = away ? awaySession : URLSession.shared
        for attempt in 0..<3 {
            guard let (data, response) = try? await session.data(for: GuestTunnelToken.request(url)),
                  let http = response as? HTTPURLResponse else { return nil }
            if http.statusCode == 200 {
                if away { KeptArtwork.keep(data, from: http, as: home) }
                return data
            }
            guard http.statusCode == 503, attempt < 2 else { return nil }
            let wait = http.value(forHTTPHeaderField: "Retry-After").flatMap(Int.init) ?? 5
            // Cancelled (scrolled away, nobody else waiting): stop rather than ask again.
            guard (try? await Task.sleep(for: .seconds(min(max(wait, 1), 10)))) != nil else { return nil }
        }
        return nil
    }

    /// Artwork from the server's address for away from home: as the shared session, but keeping nothing itself
    /// (what it fetches is kept under the home address instead).
    private static let awaySession: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.urlCache = nil
        return URLSession(configuration: configuration)
    }()

    private func abandon(_ url: URL, _ task: Task<PlatformImage?, Never>) {
        guard var load = inFlight[url], load.task == task else { return }
        load.waiters -= 1
        if load.waiters > 0 {
            inFlight[url] = load
        } else {
            task.cancel()
            inFlight[url] = nil
        }
    }

    func data(_ url: URL) async -> Data? {
        try? await URLSession.shared.data(for: GuestTunnelToken.request(url)).0
    }
}

/// An image from the server that fills its frame, with a quiet placeholder and a short
/// fade once it arrives. Cached images appear immediately with no fade.
struct RemoteImage<Placeholder: View>: View {
    let url: URL?
    var contentMode: ContentMode = .fill
    @ViewBuilder var placeholder: () -> Placeholder

    @State private var loaded: PlatformImage?
    @State private var loadedURL: URL?

    var body: some View {
        let image = (loadedURL == url ? loaded : nil) ?? url.flatMap(ImageCache.shared.image(for:))
        ZStack {
            if let image {
                Image(platformImage: image)
                    .resizable()
                    .aspectRatio(contentMode: contentMode)
                    .transition(.opacity.animation(.easeOut(duration: 0.25)))
            } else {
                placeholder()
            }
        }
        .task(id: url) {
            guard let url else { return }
            // Shown from the memory cache: held here too, so the cache letting it go (it does under memory pressure)
            // doesn't turn a picture on screen back into its placeholder, with nothing to load it again.
            if let cached = ImageCache.shared.image(for: url) {
                loaded = cached
                loadedURL = url
                return
            }
            let image = await ImageLoader.shared.load(url)
            guard !Task.isCancelled else { return }
            withAnimation(.easeOut(duration: 0.25)) {
                loaded = image
                loadedURL = url
            }
        }
    }
}

extension RemoteImage where Placeholder == Color {
    init(url: URL?, contentMode: ContentMode = .fill) {
        self.init(url: url, contentMode: contentMode) { Color.seat }
    }
}
