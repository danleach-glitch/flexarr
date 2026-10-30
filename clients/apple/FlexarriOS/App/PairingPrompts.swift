import FlexarrKit
import Foundation
import Observation

private let log = FlexarrLog("pairing")

/// Devices asking to connect (an Apple TV, another phone), for a phone signed in as the main profile to allow there
/// and then, rather than someone typing their code into the web app. The command stream says as each starts waiting;
/// the list is also fetched when the app comes to the front or a "wants to connect" notification is opened, so none
/// is missed. One is asked about at a time; the rest wait their turn. Settings lists them all, from the same model, so
/// allowing one in either place answers it in both.
///
/// Who may allow them is the server's call: it only tells the main profile's sign-ins, and refuses the list (403) to
/// anyone else, after which this stops asking until the sign-in changes.
@MainActor
@Observable
final class PairingPrompts {
    enum Step: Equatable {
        case asking
        case working
        /// Allowed: says so for a moment, then goes.
        case allowed
        case failed(String)
    }

    /// Waiting, in the order they asked (or the one a notification was opened for first).
    private(set) var waiting: [PairingRequest] = []
    /// Where each has got to, by code; asking unless said otherwise. Kept by code, so the server sending one again
    /// (its code kept going, with a later time) doesn't interrupt allowing it.
    private(set) var steps: [String: Step] = [:]
    /// Swiped away: still waiting (the web app or another phone can still allow it), just not asked about here again
    /// unless its notification is opened.
    private(set) var setAside: Set<String> = []
    /// Said when a notification is opened for a device that's no longer waiting.
    var notice: String?

    /// The server refused this sign-in the list: it isn't the main profile.
    private(set) var refused = false
    /// The server keeps a list of them (0.56 on). Nil until asked.
    private(set) var listKept: Bool?

    @ObservationIgnored private var client: FlexarrClient?
    /// Drops each as its code runs out.
    @ObservationIgnored private var expiry: Task<Void, Never>?

    /// The one to ask about now.
    /// (Not one turned away a moment ago and asking again by itself: that one is only listed, in Settings.)
    var current: PairingRequest? { waiting.first { $0.asks && !setAside.contains($0.code) } }
    /// Where the one on screen has got to.
    var step: Step { current.map { step(for: $0.code) } ?? .asking }

    func step(for code: String) -> Step { steps[code] ?? .asking }

    /// Follows the sign-in. A different one (another server, profile or sign-in) starts over.
    func use(_ client: FlexarrClient?) {
        guard client?.baseURL != self.client?.baseURL || client?.apiKey != self.client?.apiKey
            || client?.profileToken != self.client?.profileToken else { return }
        self.client = client
        refused = false
        listKept = nil
        waiting = []
        steps = [:]
        setAside = []
        expiry?.cancel()
    }

    /// From the command stream.
    func received(_ event: DeviceEvent) {
        switch event {
        case .pairing(let request): add(request)
        case .pairingEnded(let code): stoppedWaiting(code)
        case .command: break
        }
    }

    /// Asks the server who's waiting: anything the stream said while the app was away is caught up on, and anything
    /// that stopped waiting meanwhile goes.
    func catchUp() async {
        guard let client, !refused else { return }
        do {
            let kept = try await client.waitingPairingsIfKept()
            guard client.apiKey == self.client?.apiKey, client.profileToken == self.client?.profileToken else { return }
            listKept = kept != nil
            let list = kept ?? []
            let codes = Set(list.map(\.code))
            for request in waiting where !codes.contains(request.code) { stoppedWaiting(request.code) }
            for request in list { add(request) }
            // Notifications for any that stopped waiting while the app was away (allowed in the web app, say).
            if kept != nil { PushNotifications.removePairingNotifications(except: codes) }
        } catch FlexarrClientError.http(status: 403, _) {
            refused = true
        } catch {
            log.info("Couldn't fetch the devices waiting to connect", error: error)
        }
    }

    /// A "wants to connect" notification was opened: asks about that device now, or says it's gone.
    func open(code: String) async {
        setAside.remove(code)
        await catchUp()
        if refused {
            notice = "Switch to the main profile to allow it."
            return
        }
        guard let index = waiting.firstIndex(where: { $0.code == code }) else {
            notice = "That device has stopped waiting."
            return
        }
        // Not while another is being allowed: it waits its turn.
        guard index != 0, step != .working, step != .allowed else { return }
        let request = waiting.remove(at: index)
        waiting.insert(request, at: 0)
    }

    /// Lets the device in (the one on screen, or the one with `code`), says so for a moment, then moves on.
    func allow(_ code: String? = nil) async {
        guard let client, let request = request(code), !isBusy(request.code) else { return }
        steps[request.code] = .working
        do {
            try await client.approvePairing(code: request.code)
            steps[request.code] = .allowed
            try? await Task.sleep(for: .seconds(2))
            if step(for: request.code) == .allowed { remove(request.code) }
        } catch FlexarrClientError.http(status: 404, _) {
            steps[request.code] = .failed("It's no longer waiting. It may have expired; the device shows a new code.")
        } catch {
            steps[request.code] = .failed(ConnectionError(error).message)
        }
    }

    /// Turns the device away (the one on screen, or the one with `code`).
    func decline(_ code: String? = nil) async {
        guard let client, let request = request(code), !isBusy(request.code) else { return }
        steps[request.code] = .working
        do {
            try await client.declinePairing(code: request.code)
            remove(request.code)
        } catch FlexarrClientError.http(status: 404, _) {
            remove(request.code) // Gone already, which is what was wanted.
        } catch {
            steps[request.code] = .failed(ConnectionError(error).message)
        }
    }

    /// Swiped away. Still waiting, unless it was only saying it had connected.
    func setAsideCurrent() {
        guard let code = current?.code else { return }
        if step(for: code) == .allowed {
            remove(code)
        } else {
            setAside.insert(code)
            steps[code] = nil
        }
    }

    /// Allowed somewhere (a notification's button, say), turned away or run out. One saying it's connected stays for
    /// its moment.
    func stoppedWaiting(_ code: String) {
        if step(for: code) == .allowed { return }
        remove(code)
    }

    private func request(_ code: String?) -> PairingRequest? {
        guard let code else { return current }
        return waiting.first { $0.code == code }
    }

    private func isBusy(_ code: String) -> Bool {
        step(for: code) == .working || step(for: code) == .allowed
    }

    /// New, or the same one again with a later time (its code kept going): that replaces it, wherever it's got to.
    private func add(_ request: PairingRequest) {
        guard request.expiresAt > .now else { return }
        if let index = waiting.firstIndex(where: { $0.code == request.code }) {
            waiting[index] = request
        } else {
            waiting.append(request)
        }
        scheduleExpiry()
    }

    private func remove(_ code: String) {
        waiting.removeAll { $0.code == code }
        setAside.remove(code)
        steps[code] = nil
        PushNotifications.removePairingNotifications(for: code)
        scheduleExpiry()
    }

    /// Wakes as the next code runs out (going by the latest time the server gave for it) and drops it.
    private func scheduleExpiry() {
        expiry?.cancel()
        guard let next = waiting.map(\.expiresAt).filter({ $0 > .now }).min() else { return }
        expiry = Task { [weak self] in
            try? await Task.sleep(for: .seconds(max(0, next.timeIntervalSinceNow)))
            guard !Task.isCancelled, let self else { return }
            for code in waiting.filter({ $0.expiresAt <= .now }).map(\.code) { stoppedWaiting(code) }
            scheduleExpiry()
        }
    }
}

extension PairingRequest {
    /// "Apple TV · asking from your home network (192.168.1.70)". Nil when the server says neither.
    var whereFrom: String? {
        let address = requestedFrom.map { " (\($0))" } ?? ""
        let asking: String? = switch zone?.lowercased() {
        case "local": "asking from your home network\(address)"
        case "tailscale": "asking over Tailscale\(address)"
        default: requestedFrom.map { "asking from \($0)" }
        }
        guard let kind else { return asking.map { $0.prefix(1).uppercased() + $0.dropFirst() } }
        return asking.map { "\(kind) · \($0)" } ?? kind
    }

    /// "Apple TV · home network", for a row in a list.
    var kindAndNetwork: String? {
        let network: String? = switch zone?.lowercased() {
        case "local": "home network"
        case "tailscale": "Tailscale"
        default: nil
        }
        let parts = [kind, network].compactMap(\.self)
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    /// What kind of device it is, as people say it. Nil when its app doesn't say (or the server is newer than this app).
    var kind: String? {
        switch deviceType {
        case .appleTv: "Apple TV"
        case .iPhone: "iPhone"
        case .iPad: "iPad"
        case .androidPhone: "Android phone"
        case .androidTablet: "Android tablet"
        case .androidTv: "Android TV"
        case .unknown, nil: nil
        }
    }

    /// Its symbol: a television when it doesn't say.
    var symbol: String {
        switch deviceType {
        case .appleTv: "appletv"
        case .iPhone: "iphone"
        case .iPad: "ipad"
        case .androidPhone: "smartphone"
        case .androidTablet: "ipad.landscape"
        case .androidTv, .unknown, nil: "tv"
        }
    }
}
