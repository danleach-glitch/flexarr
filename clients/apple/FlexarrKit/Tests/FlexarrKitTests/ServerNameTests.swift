import Foundation
import Testing
@testable import FlexarrKit

/// The server's name: read from the server and from invites, remembered with the sign-in, and the "Dan's Flexarr"
/// it falls back to.
struct ServerNameTests {
    private func status(_ extra: String) throws -> SystemStatus {
        let json = #"{"appName":"Flexarr","version":"0.61.0","osDescription":"macOS","runtimeVersion":".NET 10","startTime":"2026-11-04T04:10:44.87+00:00""# + extra + "}"
        return try FlexarrClient.decoder.decode(SystemStatus.self, from: Data(json.utf8))
    }

    @Test func statusHasTheNameOnlyFromServersThatSendIt() throws {
        #expect(try status(#","serverName":"Smith House""#).serverName == "Smith House")
        #expect(try status("").serverName == nil)
    }

    private func invite(_ fields: [String: Any]) throws -> GuestInvite {
        var base: [String: Any] = ["v": 1, "n": "Dan", "s": "server-1", "a": "100.101.102.103:8383", "k": "tskey-auth-abc",
                                   "t": "token-123", "g": "Gran’s iPad", "p": "Gran", "e": "2099-01-01T00:00:00Z"]
        base.merge(fields) { _, new in new }
        let encoded = try JSONSerialization.data(withJSONObject: base).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
        return try #require(GuestInvite.parse(encoded))
    }

    @Test func anInviteCallsTheServerByItsName_OrByWhoseItIs() throws {
        #expect(try invite(["m": "Smith House"]).serverTitle == "Smith House")
        #expect(try invite(["m": "  "]).serverTitle == "Dan’s Flexarr")
        #expect(try invite([:]).serverTitle == "Dan’s Flexarr")
        #expect(try invite([:]).serverName == nil)
    }

    @Test func aGuestSavedBeforeNamesStillReads() throws {
        let old = #"{"target":"100.101.102.103:8383","hostname":"flexarr-guest-gran","ownerName":"Dan"}"#
        let tunnel = try JSONDecoder().decode(GuestTunnelInfo.self, from: Data(old.utf8))
        #expect(tunnel.serverName == nil)
        #expect(tunnel.serverTitle == "Dan’s Flexarr")
        #expect(GuestTunnelInfo.title(serverName: nil, ownerName: " ") == "this Flexarr")
        #expect(GuestTunnelInfo.title(serverName: "Smith House", ownerName: "Dan") == "Smith House")
    }

    @Test func theNameIsRememberedForTheServerItCameFrom() throws {
        let suite = "name-test-\(UUID().uuidString)"
        let store = CredentialStore(service: "io.github.flexarr.test.\(UUID().uuidString)", suiteName: suite)
        defer { store.clear(); UserDefaults().removePersistentDomain(forName: suite) }

        do {
            try store.save(ServerCredentials(address: URL(string: "http://192.168.1.106:8383")!, apiKey: "key"))
        } catch {
            return // No Keychain for command-line tests on every machine.
        }
        store.saveServerName("Smith House")
        #expect(store.savedServerName == "Smith House")

        // Signing in to the same server again keeps it; another server's sign-in doesn't inherit it.
        try store.save(ServerCredentials(address: URL(string: "http://192.168.1.106:8383")!, apiKey: "key"))
        #expect(store.savedServerName == "Smith House")
        try store.save(ServerCredentials(address: URL(string: "http://192.168.1.20:8383")!, apiKey: "other"))
        #expect(store.savedServerName == nil)

        store.saveServerName("  ")
        #expect(store.savedServerName == nil)
        store.saveServerName("Cabin")
        store.clear()
        #expect(store.savedServerName == nil)
    }

    @Test func sentencesPickAWholeWordingForEachKindOfName() {
        let leave = "{kind, select, named {Leave {server}?} owner {Leave {owner}’s Flexarr?} other {Leave this Flexarr?}}"
        #expect(t(leave, ServerNaming(name: "Smith House", owner: "Dan").values) == "Leave Smith House?")
        #expect(t(leave, ServerNaming(name: " ", owner: "Dan").values) == "Leave Dan’s Flexarr?")
        #expect(t(leave, ServerNaming(name: nil, owner: nil).values) == "Leave this Flexarr?")
        #expect(ServerNaming(name: nil, owner: "Dan").title == "Dan’s Flexarr")
    }

    @Test func aGuestOnlySeesANameSomeoneChose() throws {
        let unnamed = try status(#","serverName":"Dans-Mac-Mini","serverNamed":false"#)
        #expect(unnamed.serverName(forGuest: true) == nil)
        #expect(unnamed.serverName(forGuest: false) == "Dans-Mac-Mini")
        let named = try status(#","serverName":"Smith House","serverNamed":true"#)
        #expect(named.serverName(forGuest: true) == "Smith House")
    }
}
