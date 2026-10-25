import FlexarrKit
import UIKit

/// A session's scrubbing previews: the server's sheets of thumbnails, fetched as the viewer scrubs. The manifest is
/// asked for again every so often until the server has made the previews (a new file takes a minute).
@MainActor
final class TrickplayPreviews {
    private let client: FlexarrClient
    private let sessionId: String
    private var manifest: TrickplayManifest?
    private var sheets: [Int: UIImage] = [:]
    private var loading: Set<Int> = []
    private var task: Task<Void, Never>?
    /// A sheet arrived: whatever is showing a preview should ask again.
    var onLoaded: (() -> Void)?

    init(client: FlexarrClient, sessionId: String) {
        self.client = client
        self.sessionId = sessionId
        task = Task { [weak self] in
            for attempt in 0..<12 {
                if attempt > 0 { try? await Task.sleep(for: .seconds(20)) }
                guard let self, !Task.isCancelled else { return }
                if let manifest = try? await client.trickplay(sessionId: sessionId) {
                    self.manifest = manifest
                    self.onLoaded?()
                    return
                }
            }
        }
    }

    deinit {
        task?.cancel()
    }

    var isAvailable: Bool { manifest != nil }

    /// The thumbnail nearest `seconds`, if its sheet is here (it's fetched otherwise, and `onLoaded` says when).
    func image(at seconds: Double) -> UIImage? {
        guard let manifest, let (sheet, x, y) = manifest.thumbnail(at: seconds) else { return nil }
        guard let image = sheets[sheet] else {
            fetch(sheet)
            return nil
        }
        let scale = image.scale
        let rect = CGRect(x: CGFloat(x) * scale, y: CGFloat(y) * scale, width: CGFloat(manifest.width) * scale, height: CGFloat(manifest.height) * scale)
        guard let cropped = image.cgImage?.cropping(to: rect) else { return nil }
        return UIImage(cgImage: cropped, scale: scale, orientation: .up)
    }

    var aspectRatio: CGFloat {
        guard let manifest, manifest.height > 0 else { return 16 / 9 }
        return CGFloat(manifest.width) / CGFloat(manifest.height)
    }

    private func fetch(_ sheet: Int) {
        guard !loading.contains(sheet), let url = client.trickplaySheetURL(sessionId: sessionId, sheet: sheet) else { return }
        loading.insert(sheet)
        Task { [weak self] in
            let image = await ImageLoader.shared.load(url)
            guard let self else { return }
            self.loading.remove(sheet)
            if let image {
                self.sheets[sheet] = image
                self.onLoaded?()
            }
        }
    }
}
