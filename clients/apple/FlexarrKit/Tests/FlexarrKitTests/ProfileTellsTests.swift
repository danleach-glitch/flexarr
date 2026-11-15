import Testing
@testable import FlexarrKit

/// Two profiles on one phone: what the server is told about each one's downloads waits for a sign-in as that profile.
struct ProfileTellsTests {
    @Test func whatsToBeToldOfADownloadWaitsForASignInAsItsOwnProfile() {
        let entries: Set = [ProfileTells.entry("d1", profileId: "mum"), ProfileTells.entry("d2", profileId: "kid"),
                            ProfileTells.entry("d3", profileId: "mum")]

        #expect(ProfileTells.due(entries, profileId: "mum").map(\.id).sorted() == ["d1", "d3"])
        #expect(ProfileTells.due(entries, profileId: "kid").map(\.id) == ["d2"])
        // Nobody known to be chosen (Who's watching, or woken in the background): nothing is told.
        #expect(ProfileTells.due(entries, profileId: nil).isEmpty)
        // Each comes back with its entry, to be let go once told.
        #expect(ProfileTells.due(entries, profileId: "kid").first?.entry == ProfileTells.entry("d2", profileId: "kid"))
    }

    @Test func entriesKeptBeforeTheySaidWhoseTheyWereAreToldByWhoeversSignedIn() {
        let entries: Set = ["old", ProfileTells.entry("d1", profileId: "kid")]

        #expect(ProfileTells.due(entries, profileId: "mum").map(\.id) == ["old"])
        #expect(ProfileTells.due(entries, profileId: "kid").map(\.id).sorted() == ["d1", "old"])
        #expect(ProfileTells.parse("old").profileId == nil)
        let parsed = ProfileTells.parse(ProfileTells.entry("d1", profileId: "kid"))
        #expect(parsed.profileId == "kid" && parsed.id == "d1")
    }
}
