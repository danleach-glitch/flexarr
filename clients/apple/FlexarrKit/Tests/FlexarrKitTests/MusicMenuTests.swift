import Testing
@testable import FlexarrKit

struct MusicMenuTests {
    @Test func albumCardOnAPhoneHasEverything() {
        let menu = MusicMenu(subject: .album, place: .card, platform: .phone, hasArtist: true, canDownload: true)
        #expect(menu.items == [.play, .shuffle, .playNext, .addToQueue, .addToPlaylist, .goToArtist, .download])
    }

    @Test func theTvLeavesOutPlaylistsAndDownloads() {
        let menu = MusicMenu(subject: .album, place: .card, platform: .tv, hasArtist: true, canDownload: true)
        #expect(menu.items == [.play, .shuffle, .playNext, .addToQueue, .goToArtist])
    }

    @Test func aTrackRowDoesntShuffle() {
        let menu = MusicMenu(subject: .track, place: .row, platform: .phone, hasArtist: true)
        #expect(menu.items == [.play, .playNext, .addToQueue, .addToPlaylist, .goToArtist])
    }

    @Test func anArtistHasNoArtistToGoTo() {
        let menu = MusicMenu(subject: .artist, place: .card, platform: .phone, hasArtist: true)
        #expect(menu.items == [.play, .shuffle, .playNext, .addToQueue, .addToPlaylist])
    }

    @Test func aPlaylistIsntAddedToAnother() {
        let menu = MusicMenu(subject: .playlist, place: .card, platform: .phone, canDownload: true)
        #expect(menu.items == [.play, .shuffle, .playNext, .addToQueue, .download])
    }

    @Test func pagesQueueFromTheirMenu() {
        let phone = MusicActions(kind: .album, platform: .phone, hasMusic: true, canAsk: false, canQueue: true)
        #expect(phone.menu == [.playNext, .addToQueue, .addToPlaylist])
        #expect(phone.buttons.last == .more)
        let tv = MusicActions(kind: .artist, platform: .tv, hasMusic: true, canAsk: false, canQueue: true)
        #expect(tv.menu == [.playNext, .addToQueue])
        #expect(!MusicActions(kind: .artist, platform: .tv, hasMusic: false, canAsk: true, canQueue: true).has(.more))
    }
}
