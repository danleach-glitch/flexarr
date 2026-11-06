import Foundation
import Testing
@testable import FlexarrKit

/// Which sign-in the Apple TV's Top Shelf is given: never one that would show, on the Home screen, what "Who's
/// watching?" keeps behind a PIN.
struct TopShelfSignInTests {
    private let credentials = ServerCredentials(address: URL(string: "http://192.168.1.20:8383")!, apiKey: "key", profileToken: "token")
    private let child = Profile(id: "ada", name: "Ada", color: "#f0c")
    private let parent = Profile(id: "dan", name: "Dan", color: "#a9c2ec", hasPin: true, isMain: true)

    @Test func stayingWithTheLastProfileSharesItWhoeverItIs() {
        let expected = SharedSignIn.Value(address: credentials.address, apiKey: "key", profileToken: "token")
        #expect(SharedSignIn.forTopShelf(credentials, asksWhoIsWatching: false, watching: parent) == expected)
        #expect(SharedSignIn.forTopShelf(credentials, asksWhoIsWatching: false, watching: child) == expected)
        #expect(SharedSignIn.forTopShelf(credentials, asksWhoIsWatching: false, watching: nil) == expected)
    }

    @Test func askingWhoIsWatchingKeepsAPinProfilesTitlesOffTheHomeScreen() {
        #expect(SharedSignIn.forTopShelf(credentials, asksWhoIsWatching: true, watching: parent) == nil)
        // Anyone at the TV can choose a profile without a PIN: its titles give nothing away.
        #expect(SharedSignIn.forTopShelf(credentials, asksWhoIsWatching: true, watching: child)?.profileToken == "token")
        // Not known who watched last: nothing, rather than a guess.
        #expect(SharedSignIn.forTopShelf(credentials, asksWhoIsWatching: true, watching: nil) == nil)
    }

    @Test func theServerSaysWhetherChoosingAProfileHereAsksForItsPin() throws {
        // The main profile on its owner's own Apple TV: a PIN, but choosing it here doesn't ask for it.
        let own = ##"{"id":"dan","name":"Dan","color":"#a9c2ec","hasPin":true,"isMain":true,"isCurrent":true,"hiddenLibraryIds":[],"image":null,"asksPin":false}"##
        let main = try JSONDecoder().decode(Profile.self, from: Data(own.utf8))
        #expect(SharedSignIn.forTopShelf(credentials, asksWhoIsWatching: true, watching: main) != nil)
    }

    @Test func signedOutOrAGuestSharesNothing() {
        #expect(SharedSignIn.forTopShelf(nil, asksWhoIsWatching: false, watching: child) == nil)
        let guest = GuestTunnelInfo(target: "100.101.102.103:8383", hostname: "guest", ownerName: "Dan")
        let visiting = ServerCredentials(address: URL(string: "http://127.0.0.1:50123")!, apiKey: "key", guest: guest)
        #expect(SharedSignIn.forTopShelf(visiting, asksWhoIsWatching: false, watching: child) == nil)
    }
}
