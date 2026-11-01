import Foundation
import Testing
@testable import FlexarrKit

struct HiddenNavigationTests {
    private static let base = ##""id":"owner","name":"Dan","color":"#a9c2ec","hasPin":false,"isMain":true,"isCurrent":true,"hiddenLibraryIds":[],"image":null"##

    @Test func olderServersHideNothing() throws {
        let profile = try FlexarrClient.decoder.decode(Profile.self, from: Data("{\(Self.base)}".utf8))
        #expect(profile.hiddenNavigation == nil)
        #expect(NavigationItem.allCases.allSatisfy { !profile.hidesFromMenu($0) })
    }

    @Test func newerServersSayWhatsHidden_UnknownValuesMeanNothing() throws {
        let json = "{\(Self.base),\"hiddenNavigation\":[\"live-tv\",\"watchlist\",\"something-new\"]}"
        let profile = try FlexarrClient.decoder.decode(Profile.self, from: Data(json.utf8))
        #expect(profile.hidesFromMenu(.liveTv))
        #expect(profile.hidesFromMenu(.watchlist))
        #expect(!profile.hidesFromMenu(.discover))
        #expect(!profile.hidesFromMenu(.collections))
    }
}
