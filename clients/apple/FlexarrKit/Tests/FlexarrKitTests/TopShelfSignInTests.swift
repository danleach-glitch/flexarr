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

    @Test func nobodyChosenYetAmongSeveralIsWhoIsWatchingToo() {
        // Just connected, or the profile last chosen has been removed: the server would answer as the main profile.
        let nobody = ServerCredentials(address: credentials.address, apiKey: "key")
        #expect(SharedSignIn.forTopShelf(nobody, asksWhoIsWatching: false, watching: parent, severalProfiles: true) == nil)
        #expect(SharedSignIn.forTopShelf(nobody, asksWhoIsWatching: false, watching: nil, severalProfiles: true) == nil)
        // A main profile anyone here may choose gives nothing away.
        let open = Profile(id: "dan", name: "Dan", color: "#a9c2ec", isMain: true)
        #expect(SharedSignIn.forTopShelf(nobody, asksWhoIsWatching: false, watching: open, severalProfiles: true) != nil)
        // One profile is never asked for, PIN or not; nor is anyone once a profile has been chosen.
        #expect(SharedSignIn.forTopShelf(nobody, asksWhoIsWatching: false, watching: parent, severalProfiles: false) != nil)
        #expect(SharedSignIn.forTopShelf(credentials, asksWhoIsWatching: false, watching: parent, severalProfiles: true) != nil)
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
