import Foundation
import Network
import Observation

/// A Flexarr server announcing itself on the local network.
public struct DiscoveredServer: Identifiable, Hashable, Sendable {
    public let name: String
    public let address: URL
    public var id: String { address.absoluteString }

    public init(name: String, address: URL) {
        self.name = name
        self.address = address
    }
}

/// Finds Flexarr servers on the local network: each announces "_flexarr._tcp" with its address in the TXT record
/// ("url=http://Dans-Mac-Mini.local:8383"), so there's nothing to type.
@MainActor
@Observable
public final class ServerBrowser {
    public static let serviceType = "_flexarr._tcp"

    public private(set) var servers: [DiscoveredServer] = []
    private var browser: NWBrowser?

    public init() {}

    public func start() {
        guard browser == nil else { return }
        let browser = NWBrowser(for: .bonjourWithTXTRecord(type: Self.serviceType, domain: nil), using: .tcp)
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            let found = results.compactMap(Self.server).sorted { $0.name < $1.name }
            Task { @MainActor in self?.servers = Array(Set(found)).sorted { $0.name < $1.name } }
        }
        browser.start(queue: .main)
        self.browser = browser
    }

    public func stop() {
        browser?.cancel()
        browser = nil
    }

    nonisolated static func server(_ result: NWBrowser.Result) -> DiscoveredServer? {
        guard case let .service(name, _, _, _) = result.endpoint,
              case let .bonjour(record) = result.metadata,
              let url = record["url"].flatMap(FlexarrClient.normalizeAddress) else { return nil }
        return DiscoveredServer(name: name, address: url)
    }
}
