import FlexarrKit
import Foundation
import Observation

/// App-wide state: the signed-in server, its libraries and the player.
@MainActor
@Observable
final class AppModel {
    enum Phase: Equatable {
        case signedOut
        case signedIn(ServerCredentials)
    }

    private(set) var phase: Phase
    private(set) var libraries: [Library] = []
    private(set) var librariesError: String?
    /// Bumped whenever watch state changes (playback ended, marked watched) so screens reload.
    private(set) var refreshToken = 0
    /// Set by deep links (`flexarr://item/<id>`); the selected tab's stack pushes it.
    var pendingRoute: Route?

    let store = CredentialStore()
    let player = PlaybackController()

    init() {
        #if DEBUG
        // `-FlexarrServer <url> -FlexarrApiKey <key>` launch arguments sign in directly
        // (used for simulator runs and screenshots).
        let defaults = UserDefaults.standard
        // `-FlexarrSignedOut YES` starts on the connect screen (keeps the last address).
        if defaults.bool(forKey: "FlexarrSignedOut") { store.clear() }
        if let server = defaults.string(forKey: "FlexarrServer"), let url = FlexarrClient.normalizeAddress(server) {
            let credentials = ServerCredentials(address: url, apiKey: defaults.string(forKey: "FlexarrApiKey") ?? "")
            try? store.save(credentials)
        }
        // `-FlexarrNoHistory YES` plays without reporting progress, so test runs against a
        // real server don't change its watch history.
        player.recordsHistory = !defaults.bool(forKey: "FlexarrNoHistory")
        #endif
        phase = store.load().map(Phase.signedIn) ?? .signedOut
        player.onSessionEnded = { [weak self] in self?.invalidate() }
    }

    var client: FlexarrClient? {
        if case .signedIn(let credentials) = phase { return credentials.client }
        return nil
    }

    var credentials: ServerCredentials? {
        if case .signedIn(let credentials) = phase { return credentials }
        return nil
    }

    /// Validates the address and key against `/system/status`, then saves them.
    func connect(address: String, apiKey: String) async throws {
        guard let url = FlexarrClient.normalizeAddress(address) else { throw FlexarrClientError.invalidAddress }
        let key = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let credentials = ServerCredentials(address: url, apiKey: key)
        do {
            _ = try await credentials.client.systemStatus()
        } catch {
            throw ConnectionError(error)
        }
        try store.save(credentials)
        libraries = []
        phase = .signedIn(credentials)
    }

    func signOut() {
        player.stop()
        store.clear()
        libraries = []
        phase = .signedOut
    }

    func loadLibraries() async {
        guard let client else { return }
        do {
            libraries = try await client.libraries().filter { $0.type != .music }
            librariesError = nil
        } catch {
            librariesError = ConnectionError(error).message
        }
    }

    func invalidate() {
        refreshToken += 1
    }

    func play(_ item: ItemSummary, fromBeginning: Bool = false) {
        guard let client else { return }
        player.play(itemId: item.id, preview: item, startAt: fromBeginning ? 0 : nil, client: client)
    }

    /// Plays a movie or episode directly; for a series or season, plays its next-up episode
    /// (or opens the details when everything has been watched).
    func playBest(_ item: ItemSummary, router: Router) async {
        guard let client else { return }
        switch item.kind {
        case .series, .season:
            if let next = try? await client.nextUp(item.id) {
                play(next)
            } else {
                router.push(.item(item))
            }
        default:
            play(item)
        }
    }

    func setWatched(_ item: ItemSummary, _ watched: Bool) async {
        guard let client else { return }
        try? await client.setWatched(item.id, watched: watched)
        invalidate()
    }

    func open(url: URL) {
        guard url.scheme == "flexarr", let host = url.host() else { return }
        let id = url.pathComponents.dropFirst().first ?? ""
        guard !id.isEmpty else { return }
        switch host {
        case "item": pendingRoute = .itemID(id)
        case "play":
            // flexarr://play/<id>?t=<seconds> starts at a given time.
            guard let client else { return }
            let start = URLComponents(url: url, resolvingAgainstBaseURL: false)?
                .queryItems?.first(where: { $0.name == "t" })?.value.flatMap(Double.init)
            player.play(itemId: id, preview: nil, startAt: start, client: client)
        default: break
        }
    }
}

/// Turns transport errors into sentences that say what happened and what to do.
struct ConnectionError: LocalizedError {
    let message: String

    init(_ error: Error) {
        if let error = error as? ConnectionError {
            message = error.message
        } else if let error = error as? FlexarrClientError {
            message = error.errorDescription ?? "Something went wrong."
        } else if let error = error as? URLError {
            switch error.code {
            case .cannotFindHost, .dnsLookupFailed:
                message = "Couldn't find that server. Check the address, or use its IP address instead of the name."
            case .cannotConnectToHost, .networkConnectionLost:
                message = "The server isn't answering at that address. Make sure Flexarr is running and the port is right (usually 8383)."
            case .timedOut:
                message = "The server took too long to answer. Is it on the same network as this Apple TV?"
            case .notConnectedToInternet:
                message = "This Apple TV isn't connected to a network."
            case .appTransportSecurityRequiresSecureConnection:
                message = "Plain http only works for local addresses (.local names or IP addresses). Use https for anything else."
            case .secureConnectionFailed, .serverCertificateUntrusted, .serverCertificateHasBadDate, .serverCertificateNotYetValid:
                message = "The server's https certificate isn't trusted. Try its http address on your local network."
            case .cancelled:
                message = "The request was cancelled."
            default:
                message = error.localizedDescription
            }
        } else {
            message = error.localizedDescription
        }
    }

    var errorDescription: String? { message }
}

/// Navigation destinations shared by every tab.
enum Route: Hashable {
    /// An item we already have a summary for, so the header can render before detail loads.
    case item(ItemSummary)
    case itemID(String)
    case person(id: String, name: String)
    case library(Library, LibrarySort)
}

/// One navigation stack per tab.
@MainActor
@Observable
final class Router {
    var path: [Route] = []

    func push(_ route: Route) {
        path.append(route)
    }
}
