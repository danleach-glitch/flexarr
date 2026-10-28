import AppKit

/// The folders the server may use. The sandbox only lets the app, and the server running in its sandbox, see folders
/// chosen in macOS's folder window, so each is kept as a security-scoped bookmark and opened again at every launch.
/// The web app asks for one when a library (or the podcast or recordings folder) is being set up.
@MainActor
final class MediaFolders {
    private static let defaultsKey = "MediaFolderBookmarks"

    init() {
        let bookmarks = UserDefaults.standard.array(forKey: Self.defaultsKey) as? [Data] ?? []
        var kept: [Data] = []
        for bookmark in bookmarks {
            var stale = false
            guard let url = try? URL(resolvingBookmarkData: bookmark, options: .withSecurityScope,
                                     bookmarkDataIsStale: &stale),
                  url.startAccessingSecurityScopedResource() else { continue }
            kept.append(stale ? (try? Self.bookmark(url)) ?? bookmark : bookmark)
        }
        UserDefaults.standard.set(kept, forKey: Self.defaultsKey)
    }

    /// Shows the folder window, starting in `start` if given (~ is the person's home), and keeps the folder chosen.
    /// Its path, or nil when cancelled.
    func choose(startingIn start: String?) -> String? {
        let panel = NSOpenPanel()
        if let start, !start.isEmpty {
            // In the sandbox, NSHomeDirectory() is the app's container; the panel wants the person's real home.
            let home = getpwuid(getuid()).map { String(cString: $0.pointee.pw_dir) } ?? NSHomeDirectory()
            let expanded = start.hasPrefix("~/") ? home + start.dropFirst() : start
            panel.directoryURL = URL(fileURLWithPath: expanded, isDirectory: true)
        }
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Use with Flexarr"
        panel.message = "Choose a folder for Flexarr. It can use everything inside it."
        NSApp.activate()
        guard panel.runModal() == .OK, let url = panel.url,
              let bookmark = try? Self.bookmark(url), url.startAccessingSecurityScopedResource() else { return nil }
        var bookmarks = UserDefaults.standard.array(forKey: Self.defaultsKey) as? [Data] ?? []
        bookmarks.append(bookmark)
        UserDefaults.standard.set(bookmarks, forKey: Self.defaultsKey)
        return url.path
    }

    private static func bookmark(_ url: URL) throws -> Data {
        // Read and write: podcast downloads and recordings are saved into chosen folders too.
        try url.bookmarkData(options: .withSecurityScope, includingResourceValuesForKeys: nil, relativeTo: nil)
    }
}
