import Foundation
import Security
import Testing
@testable import FlexarrKit

struct KeychainPlacementTests {
    private let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                        kSecAttrService as String: "io.github.flexarr.test",
                                        kSecAttrAccount as String: "current"]

    @Test func keepsTheQueryItWasGiven() {
        let placed = KeychainPlacement.placed(query)
        #expect(placed[kSecAttrService as String] as? String == "io.github.flexarr.test")
        #expect(placed[kSecAttrAccount as String] as? String == "current")
    }

    #if os(macOS)
    /// A signed Mac app keeps its sign-in where the iPhone does; a build without a team (as `swift test` is) can't, and
    /// keeps the file Keychain rather than failing to save at all.
    @Test func usesTheDataProtectionKeychainOnlyWhereAllowed() {
        let placed = KeychainPlacement.placed(query)
        let flagged = placed[kSecUseDataProtectionKeychain as String] as? Bool == true
        #expect(flagged == KeychainPlacement.usesDataProtectionKeychain)
    }
    #endif
}
