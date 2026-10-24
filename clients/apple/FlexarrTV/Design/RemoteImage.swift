import SwiftUI
import UIKit

/// Decoded-image memory cache. Disk caching is left to URLCache (configured at launch).
final class ImageCache: @unchecked Sendable {
    static let shared = ImageCache()
    private let cache = NSCache<NSURL, UIImage>()

    init() {
        cache.totalCostLimit = 300 * 1024 * 1024
    }

    func image(for url: URL) -> UIImage? {
        cache.object(forKey: url as NSURL)
    }

    func insert(_ image: UIImage, for url: URL) {
        let cost = Int(image.size.width * image.size.height * image.scale * image.scale * 4)
        cache.setObject(image, forKey: url as NSURL, cost: cost)
    }
}

/// Loads and decodes images off the main thread, coalescing duplicate requests.
actor ImageLoader {
    static let shared = ImageLoader()
    private struct Load {
        let task: Task<UIImage?, Never>
        var waiters: Int
    }
    private var inFlight: [URL: Load] = [:]

    /// Cancelling the caller (a poster scrolled away before it arrived) stops the download,
    /// unless another view is still waiting for the same image.
    func load(_ url: URL) async -> UIImage? {
        if let cached = ImageCache.shared.image(for: url) { return cached }
        let task: Task<UIImage?, Never>
        if var load = inFlight[url] {
            load.waiters += 1
            inFlight[url] = load
            task = load.task
        } else {
            task = Task<UIImage?, Never> {
                guard let (data, response) = try? await URLSession.shared.data(from: url),
                      (response as? HTTPURLResponse)?.statusCode == 200,
                      let image = UIImage(data: data) else { return nil }
                let decoded = await image.byPreparingForDisplay() ?? image
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

    private func abandon(_ url: URL, _ task: Task<UIImage?, Never>) {
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
        try? await URLSession.shared.data(from: url).0
    }
}

/// An image from the server that fills its frame, with a quiet placeholder and a short
/// fade once it arrives. Cached images appear immediately with no fade.
struct RemoteImage<Placeholder: View>: View {
    let url: URL?
    var contentMode: ContentMode = .fill
    @ViewBuilder var placeholder: () -> Placeholder

    @State private var loaded: UIImage?
    @State private var loadedURL: URL?

    var body: some View {
        let image = (loadedURL == url ? loaded : nil) ?? url.flatMap(ImageCache.shared.image(for:))
        ZStack {
            if let image {
                Image(uiImage: image)
                    .resizable()
                    .aspectRatio(contentMode: contentMode)
                    .transition(.opacity.animation(.easeOut(duration: 0.25)))
            } else {
                placeholder()
            }
        }
        .task(id: url) {
            guard let url, ImageCache.shared.image(for: url) == nil else { return }
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
