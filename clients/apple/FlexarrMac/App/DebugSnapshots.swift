#if DEBUG
import AppKit
import SwiftUI

/// Debug builds only: pictures of the app's own window, for the parity tour (scripts/parity/mac-shots.sh) and previews,
/// without capturing the screen or moving the pointer. `-FlexarrSnapshots <folder>` turns it on;
/// `-FlexarrSnapshotPlaces 01-home=home,03-movie=item:<id>` names each picture and where to go for it (`home`,
/// `search`, `settings`, `downloads`, `watchlist`, `library:<id or name>`, `item:<id>`, `play:<id>`; a bare place is
/// named after itself). Each is saved as `mac-<name>.png` a few seconds after it opens, and with
/// `-FlexarrSnapshotQuit YES` the app quits after the last.
///
/// macOS doesn't draw a window covered by others, so the window must be in front while this runs. The app is
/// sandboxed, so the folder is inside its container: ~/Library/Containers/<bundle id>/Data/tmp/<folder>.
@MainActor
enum DebugSnapshots {
    static weak var currentModel: AppModel?

    static func runIfAsked() {
        let defaults = UserDefaults.standard
        guard let folder = defaults.string(forKey: "FlexarrSnapshots") else { return }
        let places: [(name: String, place: String)] = (defaults.string(forKey: "FlexarrSnapshotPlaces") ?? "home")
            .split(separator: ",").map { entry in
                let parts = entry.split(separator: "=", maxSplits: 1).map(String.init)
                return parts.count == 2 ? (parts[0], parts[1]) : (parts[0], parts[0])
            }
        let wait = max(2, defaults.double(forKey: "FlexarrSnapshotWait"))
        Task {
            try? await Task.sleep(for: .seconds(wait * 2))
            for (name, place) in places {
                let playing = place.hasPrefix("play:")
                go(to: place)
                try? await Task.sleep(for: .seconds(playing ? wait * 2 : wait))
                if playing { PlayerHost.shared.screen?.showControls() }
                save(to: FileManager.default.temporaryDirectory.appending(path: folder).appending(path: "mac-\(name).png"))
                // Leave the player before the next place.
                if playing {
                    PlayerHost.shared.screen?.close()
                    try? await Task.sleep(for: .seconds(1))
                }
            }
            if defaults.bool(forKey: "FlexarrSnapshotQuit") { NSApp.terminate(nil) }
        }
    }

    private static func go(to place: String) {
        let navigation = MacNavigation.shared
        let parts = place.split(separator: ":", maxSplits: 1).map(String.init)
        let value = parts.count > 1 ? parts[1] : ""
        switch parts.first ?? "" {
        case "home": navigation.selection = .home
        case "search": navigation.selection = .search
        case "settings": navigation.selection = .settings
        case "downloads": navigation.selection = .downloads
        case "watchlist": navigation.selection = .watchlist
        case "library":
            if let model = currentModel,
               let library = model.libraries.first(where: { $0.id == value || $0.name.lowercased() == value.lowercased() }) {
                navigation.selection = .entry(.library(library))
            }
        case "item":
            navigation.selection = .home
            navigation.router(for: .home).path = [.itemID(value)]
        case "play":
            if let url = URL(string: "flexarr://play/\(value)") { currentModel?.open(url: url) }
        default:
            break
        }
    }

    private static func save(to url: URL) {
        guard let window = NSApp.windows.first(where: { $0.isVisible && $0.contentView != nil }),
              let view = window.contentView?.superview ?? window.contentView, let layer = view.layer else { return }
        if !window.occlusionState.contains(.visible) {
            print("Flexarr snapshots: the window is covered, so macOS hasn't drawn it; bring it to the front")
        }
        let scale = window.backingScaleFactor
        guard let context = CGContext(data: nil, width: Int(view.bounds.width * scale), height: Int(view.bounds.height * scale),
                                      bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return }
        // The layers as Core Animation has them (drawing the views instead leaves SwiftUI's split view blank).
        context.scaleBy(x: scale, y: scale)
        layer.render(in: context)
        guard let image = context.makeImage() else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])?.write(to: url)
    }
}
#endif
