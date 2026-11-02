import Testing
@testable import FlexarrKit

struct BookActionsTests {
    @Test func aNewBookPlaysAndCanBeFinishedAndDownloaded() {
        let actions = BookActions(hasProgress: false, finished: false, isKids: false, platform: .phone, canDownload: true)
        #expect(actions.buttons == [.play, .finished, .download])
    }

    @Test func partWayThroughOffersStartOverSecond() {
        let actions = BookActions(hasProgress: true, finished: false, isKids: false, platform: .phone, canDownload: true)
        #expect(actions.buttons == [.play, .startOver, .finished, .download])
    }

    @Test func aFinishedBookCanStartOver() {
        let actions = BookActions(hasProgress: false, finished: true, isKids: false, platform: .tv)
        #expect(actions.buttons == [.play, .startOver, .finished])
    }

    @Test func theTVNeverDownloads() {
        #expect(!BookActions(hasProgress: false, finished: false, isKids: false, platform: .tv, canDownload: true).has(.download))
    }

    @Test func childrenHaveNoFinishedButton() {
        #expect(BookActions(hasProgress: true, finished: false, isKids: true, platform: .phone).buttons == [.play, .startOver])
    }

    @Test func moreComesLastWhenThereIsAMenu() {
        #expect(BookActions(hasProgress: false, finished: false, isKids: false, platform: .phone, canDownload: true, hasMenu: true).buttons.last == .more)
    }
}
