import Foundation
import Testing
@testable import FlexarrKit

struct MusicActionsTests {
    @Test func libraryArtistOnAPhone() {
        let actions = MusicActions(kind: .artist, platform: .phone, hasMusic: true, canAsk: true)
        #expect(actions.buttons == [.play, .shuffle, .radio, .follow])
        #expect(actions.menu.isEmpty)
    }

    @Test func libraryAlbumDownloadsOnPhonesOnly() {
        let phone = MusicActions(kind: .album, platform: .phone, hasMusic: true, canDownload: true, canAsk: false)
        #expect(phone.buttons == [.play, .shuffle, .download])
        let tv = MusicActions(kind: .album, platform: .tv, hasMusic: true, canDownload: true, canAsk: false)
        #expect(tv.buttons == [.play, .shuffle])
        let notHere = MusicActions(kind: .album, platform: .phone, hasMusic: true, canDownload: false, canAsk: false)
        #expect(!notHere.has(.download))
    }

    @Test func placeholderArtistOnlyFollows() {
        // Followed before their first album: nothing to play, but Following stays.
        let actions = MusicActions(kind: .artist, platform: .tv, hasMusic: false, canAsk: true)
        #expect(actions.buttons == [.follow])
    }

    @Test func outsideAlbumOnlyRequests() {
        let actions = MusicActions(kind: .album, platform: .phone, hasMusic: false, canDownload: true, canAsk: true)
        #expect(actions.buttons == [.request])
    }

    @Test func musicRequestsOffLeaveNoFollowOrRequest() {
        #expect(MusicActions(kind: .artist, platform: .phone, hasMusic: true, canAsk: false).buttons == [.play, .shuffle, .radio])
        #expect(MusicActions(kind: .artist, platform: .phone, hasMusic: false, canAsk: false).buttons.isEmpty)
    }

    @Test func moreComesLastAndOnlyWithSomethingInIt() {
        let actions = MusicActions(kind: .album, platform: .phone, hasMusic: true, canDownload: true, canAsk: true, canDelete: true)
        #expect(actions.buttons == [.play, .shuffle, .download, .request, .more])
        #expect(actions.has(.delete))
        #expect(!MusicActions(kind: .album, platform: .phone, hasMusic: false, canAsk: true, canDelete: true).has(.more))
    }
}
