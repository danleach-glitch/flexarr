import FlexarrKit
import Foundation
import Observation

private let log = FlexarrLog("pairing")

/// Devices asking to connect (an Apple TV, another phone), for a phone signed in as the main profile to allow there
/// and then, rather than someone typing their code into the web app. The command stream says as each starts waiting;
/// the list is also fetched when the app comes to the front or a "wants to connect" notification is opened, so none
/// is missed. One is asked about at a time; the rest wait their turn.
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
    /// Where the one on screen has got to.
    private(set) var step: Step = .asking
    /// Swiped away: still waiting (the web app or another phone can still allow it), just not asked about here again
    /// unless its notification is opened.
    private(set) var setAside: Set<String> = []
    /// Said when a notification is opened for a device that's no longer waiting.
    var notice: String?

    @ObservationIgnored private var client: FlexarrClient?
    /// The server refused this sign-in the list: it isn't the main profile.
    @ObservationIgnored private var refused = false

    /// The one to ask about now.
    var current: PairingRequest? { waiting.first { !setAside.contains($0.code) } }

    /// Follows the sign-in. A different one (another server, profile or sign-in) starts over.
    func use(_ client: FlexarrClient?) {
        guard client?.baseURL != self.client?.baseURL || client?.apiKey != self.client?.apiKey
            || client?.profileToken != self.client?.profileToken else { return }
        self.client = client
        refused = false
        waiting = []
        setAside = []
        step = .asking
    }

    /// From the command stream.
    func received(_ event: DeviceEvent) {
        switch event {
        case .pairing(let request): add(request)
        case .pairingEnded(let code): ended(code)
        case .command: break
        }
    }

    /// Asks the server who's waiting: anything the stream said while the app was away is caught up on, and anything
    /// that stopped waiting meanwhile goes.
    func catchUp() async {
        guard let client, !refused else { return }
        do {
            let list = try await client.waitingPairings()
            guard client.apiKey == self.client?.apiKey, client.profileToken == self.client?.profileToken else { return }
            let codes = Set(list.map(\.code))
            for request in waiting where !codes.contains(request.code) { ended(request.code) }
            for request in list { add(request) }
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
        guard let index = waiting.firstIndex(where: { $0.code == code }) else {
            notice = "That device has stopped waiting."
            return
        }
        // Not while another is being allowed: it waits its turn.
        guard index != 0, step != .working, step != .allowed else { return }
        let request = waiting.remove(at: index)
        waiting.insert(request, at: 0)
        step = .asking
    }

    /// Lets the device in, says so for a moment, then moves on to the next.
    func allow() async {
        guard let client, let request = current, step != .working, step != .allowed else { return }
        step = .working
        do {
            try await client.approvePairing(code: request.code)
            step = .allowed
            try? await Task.sleep(for: .seconds(2))
            if current?.code == request.code, step == .allowed { remove(request.code) }
        } catch FlexarrClientError.http(status: 404, _) {
            step = .failed("It's no longer waiting. It may have expired; the device shows a new code.")
        } catch {
            step = .failed(ConnectionError(error).message)
        }
    }

    /// Turns the device away.
    func decline() async {
        guard let client, let request = current, step != .working, step != .allowed else { return }
        step = .working
        do {
            try await client.declinePairing(code: request.code)
            remove(request.code)
        } catch FlexarrClientError.http(status: 404, _) {
            remove(request.code) // Gone already, which is what was wanted.
        } catch {
            step = .failed(ConnectionError(error).message)
        }
    }

    /// Swiped away. Still waiting, unless it was only saying it had connected.
    func setAsideCurrent() {
        guard let code = current?.code else { return }
        if step == .allowed {
            remove(code)
        } else {
            setAside.insert(code)
            step = .asking
        }
    }

    /// Its code ran out.
    func expired(_ code: String) {
        ended(code)
    }

    private func add(_ request: PairingRequest) {
        guard request.expiresAt > .now else { return }
        if let index = waiting.firstIndex(where: { $0.code == request.code }) {
            waiting[index] = request
        } else {
            waiting.append(request)
        }
    }

    /// Allowed somewhere, turned away or run out. The one on screen saying it's connected stays for its moment.
    private func ended(_ code: String) {
        if current?.code == code, step == .allowed { return }
        remove(code)
    }

    private func remove(_ code: String) {
        let wasCurrent = current?.code == code
        waiting.removeAll { $0.code == code }
        setAside.remove(code)
        if wasCurrent { step = .asking }
    }
}

extension PairingRequest {
    /// "It's asking from your home network (192.168.1.70)." Nil when the server doesn't say.
    var whereFrom: String? {
        let address = requestedFrom.map { " (\($0))" } ?? ""
        switch zone?.lowercased() {
        case "local": return "It's asking from your home network\(address)."
        case "tailscale": return "It's asking over Tailscale\(address)."
        default: return requestedFrom.map { "It's asking from \($0)." }
        }
    }
}
