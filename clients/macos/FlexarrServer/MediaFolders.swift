import AppKit

/// The folders the server may use. The sandbox only lets the app, and the server running in its sandbox, see folders
/// chosen in macOS's folder window, so each is kept as a security-scoped bookmark and opened again at every launch.
/// The web app asks for one when a library (or the podcast or recordings folder) is being set up.
@MainActor
final class MediaFolders {
    private static let defaultsKey = "MediaFolderBookmarks"
    /// Bookmarks that couldn't be opened (their drive, a USB disk or a NAS, wasn't mounted yet): kept, and tried again
    /// whenever a drive mounts, rather than forgotten until the folder is chosen again.
    private var waiting: [Data] = []
    private var mountObserver: NSObjectProtocol?

    init() {
        let bookmarks = UserDefaults.standard.array(forKey: Self.defaultsKey) as? [Data] ?? []
        var saved: [Data] = []
        for bookmark in bookmarks {
            if let opened = Self.open(bookmark) {
                saved.append(opened)
            } else {
                saved.append(bookmark)
                waiting.append(bookmark)
            }
        }
        UserDefaults.standard.set(saved, forKey: Self.defaultsKey)
        // On the main queue, so it runs on the main actor.
        mountObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didMountNotification,
                                                                          object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.openWaiting() }
        }
    }

    /// A drive mounted: the folders on it that were waiting open now (the server, a child of this app, sees them too).
    private func openWaiting() {
        guard !waiting.isEmpty else { return }
        var saved = UserDefaults.standard.array(forKey: Self.defaultsKey) as? [Data] ?? []
        waiting = waiting.filter { bookmark in
            guard let opened = Self.open(bookmark) else { return true }
            if let index = saved.firstIndex(of: bookmark) { saved[index] = opened }
            return false
        }
        UserDefaults.standard.set(saved, forKey: Self.defaultsKey)
    }

    /// Resolves a bookmark and opens its folder: the bookmark to keep (renewed when stale), or nil when the folder
    /// can't be reached now.
    private static func open(_ bookmark: Data) -> Data? {
        var stale = false
        guard let url = try? URL(resolvingBookmarkData: bookmark, options: .withSecurityScope, bookmarkDataIsStale: &stale),
              url.startAccessingSecurityScopedResource() else { return nil }
        return stale ? (try? Self.bookmark(url)) ?? bookmark : bookmark
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
        // Choosing a folder again replaces its bookmark rather than adding another each time.
        var bookmarks = (UserDefaults.standard.array(forKey: Self.defaultsKey) as? [Data] ?? []).filter { saved in
            var stale = false
            let path = try? URL(resolvingBookmarkData: saved, options: .withSecurityScope, relativeTo: nil, bookmarkDataIsStale: &stale).path
            return path != url.path
        }
        bookmarks.append(bookmark)
        UserDefaults.standard.set(bookmarks, forKey: Self.defaultsKey)
        waiting = waiting.filter(bookmarks.contains)
        return url.path
    }

    private static func bookmark(_ url: URL) throws -> Data {
        // Read and write: podcast downloads and recordings are saved into chosen folders too.
        try url.bookmarkData(options: .withSecurityScope, includingResourceValuesForKeys: nil, relativeTo: nil)
    }
}
