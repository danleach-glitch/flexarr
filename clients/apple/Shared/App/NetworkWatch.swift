import Foundation
import Network

/// Whether this device has a network at all, so the app can go straight to offline mode (and a family guest's app
/// say it can't reach its host) rather than wait out a request's timeout, and try again as soon as there's one.
@MainActor
final class NetworkWatch {
    static let shared = NetworkWatch()
    private let monitor = NWPathMonitor()
    private(set) var isOffline = false
    /// The first reading has come in (it does within moments of starting).
    private var known = false
    private var waiting: [CheckedContinuation<Void, Never>] = []
    private var listeners: [@MainActor (_ offline: Bool) -> Void] = []

    private init() {
        monitor.pathUpdateHandler = { path in
            let offline = path.status != .satisfied
            Task { @MainActor in NetworkWatch.shared.changed(offline: offline) }
        }
        monitor.start(queue: .main)
    }

    /// Hears every change from then on: true when the network has gone, false when it's back.
    func onChange(_ listener: @escaping @MainActor (_ offline: Bool) -> Void) {
        listeners.append(listener)
    }

    /// Whether there's no network now. At launch it waits for the first reading, for a moment at most.
    func isOfflineNow() async -> Bool {
        if !known {
            let timer = Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(300))
                self.release()
            }
            await withCheckedContinuation { waiting.append($0) }
            timer.cancel()
        }
        return isOffline
    }

    private func release() {
        let all = waiting
        waiting = []
        all.forEach { $0.resume() }
    }

    private func changed(offline: Bool) {
        let wasKnown = known
        let changed = offline != isOffline
        known = true
        isOffline = offline
        release()
        if wasKnown && changed || !wasKnown && offline { listeners.forEach { $0(offline) } }
    }
}
