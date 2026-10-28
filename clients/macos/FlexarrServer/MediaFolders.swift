import AppKit
import Network

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

    /// Shows the folder window and keeps the folder chosen. Its path, or nil when cancelled.
    func choose() -> String? {
        let panel = NSOpenPanel()
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

/// Where the server asks for a folder (src/Flexarr.Api/Endpoints/MacAppHost.cs): a port on the loopback address and a
/// token, both passed to it at launch. Each connection sends one line, the token and "choose", and gets back the
/// chosen folder's path, or an empty line if it was cancelled or the token was wrong.
@MainActor
final class FolderRequests {
    let token = UUID().uuidString
    private let folders: MediaFolders
    private var listener: NWListener?

    init(folders: MediaFolders) {
        self.folders = folders
    }

    /// Starts listening and returns the port, once there is one.
    func start() async throws -> UInt16 {
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = NWEndpoint.hostPort(host: .ipv4(.loopback), port: .any)
        let listener = try NWListener(using: parameters)
        self.listener = listener
        listener.newConnectionHandler = { [weak self] connection in
            MainActor.assumeIsolated { self?.answer(connection) }
        }
        return try await withCheckedThrowingContinuation { continuation in
            starting = continuation
            // On the main queue, so its callbacks run on the main actor.
            listener.stateUpdateHandler = { [weak self] state in
                MainActor.assumeIsolated { self?.listenerChanged(state) }
            }
            listener.start(queue: .main)
        }
    }

    private var starting: CheckedContinuation<UInt16, Error>?

    private func listenerChanged(_ state: NWListener.State) {
        guard let starting else { return }
        switch state {
        case .ready:
            self.starting = nil
            starting.resume(returning: listener?.port?.rawValue ?? 0)
        case .failed(let error):
            self.starting = nil
            starting.resume(throwing: error)
        default:
            break
        }
    }

    private func answer(_ connection: NWConnection) {
        connection.start(queue: .main)
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1024) { [weak self] data, _, _, _ in
            let request = data.flatMap { String(data: $0, encoding: .utf8) }?
                .trimmingCharacters(in: .whitespacesAndNewlines)
            Task { @MainActor in
                guard let self else { return connection.cancel() }
                let path = request == "\(self.token) choose" ? self.folders.choose() : nil
                connection.send(content: Data(((path ?? "") + "\n").utf8), completion: .contentProcessed { _ in
                    connection.cancel()
                })
            }
        }
    }
}
