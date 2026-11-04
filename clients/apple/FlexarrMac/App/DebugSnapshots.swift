#if DEBUG
import AppKit
import SwiftUI

/// Debug builds only: pictures of the app's own window, saved to a folder, for previews and the parity tour without
/// capturing the screen or moving the pointer (the Mac is someone's desk). `-FlexarrSnapshots <folder>` turns it on;
/// `-FlexarrSnapshotPlaces home,search,settings` (sidebar places, or `library:<name>`) are visited in turn, each saved
/// as `mac-<n>-<place>.png` a few seconds after it opens, and the app quits after the last. The app is sandboxed, so
/// the folder is inside its container: ~/Library/Containers/<bundle id>/Data/tmp/<folder>.
@MainActor
enum DebugSnapshots {
    static func runIfAsked() {
        let defaults = UserDefaults.standard
        guard let folder = defaults.string(forKey: "FlexarrSnapshots") else { return }
        let places = (defaults.string(forKey: "FlexarrSnapshotPlaces") ?? "home").split(separator: ",").map(String.init)
        let wait = max(2, defaults.double(forKey: "FlexarrSnapshotWait"))
        Task {
            try? await Task.sleep(for: .seconds(wait * 2))
            for (index, place) in places.enumerated() {
                go(to: place)
                try? await Task.sleep(for: .seconds(wait))
                save(to: FileManager.default.temporaryDirectory.appending(path: folder).appending(path: String(format: "mac-%02d-%@.png", index + 1, place.replacingOccurrences(of: ":", with: "-"))))
            }
            if defaults.bool(forKey: "FlexarrSnapshotQuit") { NSApp.terminate(nil) }
        }
    }

    private static func go(to place: String) {
        let navigation = MacNavigation.shared
        switch place {
        case "home": navigation.selection = .home
        case "search": navigation.selection = .search
        case "settings": navigation.selection = .settings
        case "downloads": navigation.selection = .downloads
        case "watchlist": navigation.selection = .watchlist
        default:
            if place.hasPrefix("library:") {
                let name = place.dropFirst("library:".count).lowercased()
                if let model = currentModel, let library = model.libraries.first(where: { $0.name.lowercased() == name }) {
                    navigation.selection = .entry(.library(library))
                }
            } else if place.hasPrefix("route:"), let model = currentModel {
                // route:item:<id> opens a title on Home's stack.
                let id = place.dropFirst("route:item:".count)
                navigation.selection = .home
                navigation.router(for: .home).push(.itemID(String(id)))
                _ = model
            }
        }
    }

    /// The app model, for finding a library by name.
    static weak var currentModel: AppModel?

    private static func save(to url: URL) {
        guard let window = NSApp.windows.first(where: { $0.isVisible && $0.contentView != nil }),
              let view = window.contentView?.superview ?? window.contentView, let layer = view.layer else { return }
        let scale = window.backingScaleFactor
        let size = CGSize(width: view.bounds.width * scale, height: view.bounds.height * scale)
        guard let context = CGContext(data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return }
        // The layers as Core Animation has them (drawing the views instead leaves SwiftUI's split view blank).
        context.scaleBy(x: scale, y: scale)
        layer.render(in: context)
        guard let image = context.makeImage() else { return }
        let rep = NSBitmapImageRep(cgImage: image)
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }
}
#endif
