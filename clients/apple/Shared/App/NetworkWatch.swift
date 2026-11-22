import FlexarrKit
import Foundation
import Network

/// The system's Local Network permission, asked for at a moment that makes sense: as a household device learns its
/// server's home address (joining, or the first time `/me` gives it), rather than on its first try of that address,
/// which could be in the middle of something playing. A brief look for Flexarr servers nearby, as the connect screen
/// does, is what has the system ask; once answered it never asks again, and the look finds nothing that's used.
@MainActor
enum LocalNetworkAccess {
    private static var asked = false

    static func ask() {
        guard !asked else { return }
        asked = true
        let browser = ServerBrowser()
        browser.start()
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(10))
            browser.stop()
        }
    }
}

/// Whether this device has a network at all, so the app can go straight to offline mode (and a family guest's app
/// say it can't reach its host) rather than wait out a request's timeout, and try again as soon as there's one.
@MainActor
final class NetworkWatch {
    static let shared = NetworkWatch()
    private let monitor = NWPathMonitor()
    private(set) var isOffline = false
    /// On mobile data, or a network that says it's expensive or limited (Low Data Mode, a phone's hotspot): the speed
    /// test fetches less (see `SpeedTest`).
    private(set) var isExpensive = false
    /// The interfaces of the last reading, to tell another network from the same one read again.
    private var interfaces: [String] = []
    /// The first reading has come in (it does within moments of starting).
    private var known = false
    private var waiting: [CheckedContinuation<Void, Never>] = []
    private var listeners: [@MainActor (_ offline: Bool) -> Void] = []

    private init() {
        monitor.pathUpdateHandler = { path in
            let offline = path.status != .satisfied
            let expensive = path.isExpensive || path.isConstrained || path.usesInterfaceType(.cellular)
            let interfaces = path.availableInterfaces.map(\.name)
            Task { @MainActor in
                let watch = NetworkWatch.shared
                watch.isExpensive = expensive
                // Another network (Wi-Fi to mobile data, another Wi-Fi): the speed it had says nothing of this one.
                let moved = watch.known && interfaces != watch.interfaces && !offline && !watch.isOffline
                if watch.known, interfaces != watch.interfaces || offline != watch.isOffline { SpeedTest.shared.networkChanged() }
                watch.interfaces = interfaces
                watch.changed(offline: offline, moved: moved)
            }
        }
        monitor.start(queue: .main)
    }

    /// Hears every change from then on: true when the network has gone, false when it's back or the device has moved
    /// to another one (Wi-Fi to mobile data and back), so a household device asks again whether home answers.
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

    /// `moved`: online before and after, on other interfaces.
    private func changed(offline: Bool, moved: Bool) {
        let wasKnown = known
        let changed = offline != isOffline
        known = true
        isOffline = offline
        release()
        if wasKnown && (changed || moved) || !wasKnown && offline { listeners.forEach { $0(offline) } }
    }
}
