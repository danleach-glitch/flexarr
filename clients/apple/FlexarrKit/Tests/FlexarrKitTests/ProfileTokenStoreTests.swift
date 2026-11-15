import Foundation
import Testing
@testable import FlexarrKit

/// The chosen profile's token is kept in the Keychain beside the API key, not in UserDefaults (which go with backups).
struct ProfileTokenStoreTests {
    @Test func theProfileTokenIsKeptInTheKeychain_AndMovedThereFromDefaults() throws {
        let suite = "profile-test-\(UUID().uuidString)"
        let service = "io.github.flexarr.test.\(UUID().uuidString)"
        let store = CredentialStore(service: service, suiteName: suite)
        defer { store.clear(); UserDefaults().removePersistentDomain(forName: suite) }
        let address = URL(string: "http://192.168.1.2:8383")!
        do {
            try store.save(ServerCredentials(address: address, apiKey: "key", profileToken: "sam.signature"))
        } catch {
            return // No Keychain for command-line tests on every machine.
        }
        let defaults = try #require(UserDefaults(suiteName: suite))
        let oldKey = "\(service).address.profile"
        #expect(defaults.string(forKey: oldKey) == nil)
        #expect(store.load()?.profileToken == "sam.signature")

        store.saveProfile(nil)
        #expect(store.load()?.profileToken == nil)

        // Where an earlier version left it: read once, then only from the Keychain.
        defaults.set("kid.signature", forKey: oldKey)
        #expect(store.load()?.profileToken == "kid.signature")
        #expect(defaults.string(forKey: oldKey) == nil)
        #expect(store.load()?.profileToken == "kid.signature")

        store.clear()
        try store.save(ServerCredentials(address: address, apiKey: "key"))
        #expect(store.load()?.profileToken == nil, "signing out takes the profile with it")
    }
}
