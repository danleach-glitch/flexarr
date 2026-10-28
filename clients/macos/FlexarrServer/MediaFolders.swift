import AppKit
import Observation

/// The folders the server may read. The sandbox only lets the app see folders chosen in an open panel, so each is
/// kept as a security-scoped bookmark and opened again at every launch, before the server starts.
@MainActor
@Observable
final class MediaFolders {
    private static let defaultsKey = "MediaFolderBookmarks"

    private(set) var urls: [URL] = []

    init() {
        let bookmarks = UserDefaults.standard.array(forKey: Self.defaultsKey) as? [Data] ?? []
        var kept: [Data] = []
        for bookmark in bookmarks {
            var stale = false
            guard let url = try? URL(resolvingBookmarkData: bookmark, options: .withSecurityScope,
                                     bookmarkDataIsStale: &stale),
                  url.startAccessingSecurityScopedResource() else { continue }
            urls.append(url)
            kept.append(stale ? (try? Self.bookmark(url)) ?? bookmark : bookmark)
        }
        UserDefaults.standard.set(kept, forKey: Self.defaultsKey)
    }

    /// Asks for a folder. Returns whether one was added, so the server can be restarted to see it.
    func choose() -> Bool {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = true
        panel.prompt = "Share with Flexarr"
        panel.message = "Choose the folders that hold your films, TV, music or podcasts."
        NSApp.activate()
        guard panel.runModal() == .OK else { return false }

        var added = false
        for url in panel.urls where !urls.contains(url) {
            guard let bookmark = try? Self.bookmark(url), url.startAccessingSecurityScopedResource() else { continue }
            urls.append(url)
            var bookmarks = UserDefaults.standard.array(forKey: Self.defaultsKey) as? [Data] ?? []
            bookmarks.append(bookmark)
            UserDefaults.standard.set(bookmarks, forKey: Self.defaultsKey)
            added = true
        }
        return added
    }

    func remove(_ url: URL) {
        guard let index = urls.firstIndex(of: url) else { return }
        url.stopAccessingSecurityScopedResource()
        urls.remove(at: index)
        var bookmarks = UserDefaults.standard.array(forKey: Self.defaultsKey) as? [Data] ?? []
        if index < bookmarks.count { bookmarks.remove(at: index) }
        UserDefaults.standard.set(bookmarks, forKey: Self.defaultsKey)
    }

    private static func bookmark(_ url: URL) throws -> Data {
        try url.bookmarkData(options: [.withSecurityScope, .securityScopeAllowOnlyReadAccess],
                             includingResourceValuesForKeys: nil, relativeTo: nil)
    }
}
