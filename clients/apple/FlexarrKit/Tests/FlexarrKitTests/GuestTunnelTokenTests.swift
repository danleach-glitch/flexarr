import Foundation
import Testing
@testable import FlexarrKit

/// The guest tunnel's per-launch token: sent to the tunnel's own address, and nowhere else.
@Suite(.serialized) struct GuestTunnelTokenTests {
    @Test func onlyTheTunnelsAddressIsSentTheToken() {
        defer { GuestTunnelToken.tunnel(listeningOn: nil) }
        let tunnel = URL(string: "http://127.0.0.1:51234/api/v1/items")!
        #expect(GuestTunnelToken.headers(for: tunnel).isEmpty) // not running yet
        GuestTunnelToken.tunnel(listeningOn: 51234)
        #expect(GuestTunnelToken.headers(for: tunnel) == ["X-Flexarr-Tunnel": GuestTunnelToken.current])
        #expect(GuestTunnelToken.headers(for: URL(string: "http://127.0.0.1:8383/api/v1/items")!).isEmpty)
        #expect(GuestTunnelToken.headers(for: URL(string: "http://192.168.1.10:51234/api/v1/items")!).isEmpty)
        #expect(GuestTunnelToken.headers(for: URL(string: "https://flexarr.example.com/api/v1/items")!).isEmpty)
        #expect(GuestTunnelToken.current.count == 64)
    }

    @Test func requestsThroughTheTunnelCarryIt() {
        defer { GuestTunnelToken.tunnel(listeningOn: nil) }
        GuestTunnelToken.tunnel(listeningOn: 51235)
        let guest = FlexarrClient(baseURL: URL(string: "http://127.0.0.1:51235")!, apiKey: "key")
        #expect(guest.downloadFileRequest("x").value(forHTTPHeaderField: "X-Flexarr-Tunnel") == GuestTunnelToken.current)
        let home = FlexarrClient(baseURL: URL(string: "http://192.168.1.10:8383")!, apiKey: "key")
        #expect(home.downloadFileRequest("x").value(forHTTPHeaderField: "X-Flexarr-Tunnel") == nil)
    }
}
