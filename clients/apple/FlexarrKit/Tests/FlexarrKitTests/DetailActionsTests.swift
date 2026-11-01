import Testing
@testable import FlexarrKit

struct DetailActionsTests {
    private func actions(kind: DetailActions.Kind = .movie, progress: Bool = false, watched: Bool = false, trailer: Bool = true,
                         kids: Bool = false, platform: DetailActions.Platform = .phone, trailers: Bool = true,
                         download: Bool = false, subtitles: Bool = false) -> DetailActions {
        DetailActions(kind: kind, hasProgress: progress, watched: watched, hasTrailer: trailer, isKids: kids, platform: platform,
                      canPlayTrailers: trailers, canDownload: download, canSearchSubtitles: subtitles)
    }

    @Test func aFreshMovie() {
        let a = actions()
        #expect(a.buttons == [.play, .trailer, .watched, .watchlist])
        #expect(a.menu.isEmpty)
    }

    @Test func aPartWatchedMovieCanStartOverOrBeForgotten() {
        let a = actions(progress: true)
        #expect(a.buttons == [.play, .startOver, .trailer, .watched, .watchlist, .more])
        #expect(a.menu == [.markUnwatched])
    }

    @Test func aWatchedMovieUnmarksWithItsOwnButtonNotTheMenu() {
        let a = actions(watched: true)
        #expect(a.buttons == [.play, .trailer, .watched, .watchlist])
        #expect(!a.has(.markUnwatched))
        // Rewatching part way: still only the one Mark as unwatched, the Watched button's.
        #expect(!actions(progress: true, watched: true).has(.markUnwatched))
    }

    @Test func showsShuffle() {
        let a = actions(kind: .show)
        #expect(a.buttons == [.play, .shuffle, .trailer, .watched, .watchlist])
        #expect(!actions().has(.shuffle))
        #expect(!actions(kind: .episode).has(.shuffle))
    }

    @Test func episodesGoToTheirShowFromTheMenu() {
        let a = actions(kind: .episode, trailer: false)
        #expect(a.buttons == [.play, .watched, .watchlist, .more])
        #expect(a.menu == [.goToShow])
    }

    @Test func childrenGetNoTrailerWatchedOrWatchlist() {
        let a = actions(progress: true, kids: true, subtitles: true)
        #expect(a.buttons == [.play, .startOver])
        #expect(a.menu.isEmpty)
        #expect(actions(kind: .episode, kids: true).menu == [.goToShow])
    }

    @Test func aTVWithoutYouTubeHasNoTrailer() {
        #expect(!actions(platform: .tv, trailers: false).has(.trailer))
        #expect(actions(platform: .tv, trailers: true).has(.trailer))
        #expect(!actions(trailer: false).has(.trailer))
    }

    @Test func phonesDownloadTVsDont() {
        #expect(actions(download: true).buttons == [.play, .trailer, .watched, .watchlist, .download])
        #expect(!actions(platform: .tv, download: true).has(.download))
        #expect(!actions(download: false).has(.download))
    }

    @Test func theMenuKeepsItsOrder() {
        let a = actions(kind: .episode, progress: true, subtitles: true)
        #expect(a.menu == [.markUnwatched, .subtitles, .goToShow])
        #expect(a.buttons.last == .more)
    }
}
