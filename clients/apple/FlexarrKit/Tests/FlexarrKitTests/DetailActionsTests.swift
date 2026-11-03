import Testing
@testable import FlexarrKit

struct DetailActionsTests {
    private func actions(kind: DetailActions.Kind = .movie, progress: Bool = false, watched: Bool = false, trailer: Bool = true,
                         kids: Bool = false, platform: DetailActions.Platform = .phone, trailers: Bool = true,
                         download: Bool = false, subtitles: Bool = false, watchlist: Bool = true) -> DetailActions {
        DetailActions(kind: kind, hasProgress: progress, watched: watched, hasTrailer: trailer, isKids: kids, platform: platform,
                      canPlayTrailers: trailers, canDownload: download, canSearchSubtitles: subtitles, canWatchlist: watchlist)
    }

    @Test func aFreshMovie() {
        let a = actions()
        #expect(a.buttons == [.play, .trailer, .watched, .watchlist])
        #expect(a.menu.isEmpty)
    }

    @Test func aPartWatchedMovieCanStartOver() {
        let a = actions(progress: true, platform: .tv)
        #expect(a.buttons == [.play, .startOver, .trailer, .watched, .watchlist])
        #expect(a.menu.isEmpty)
    }

    @Test func aWatchedMovieUnmarksWithItsOwnButton() {
        let a = actions(watched: true)
        #expect(a.buttons == [.play, .trailer, .watched, .watchlist])
        #expect(a.menu.isEmpty)
    }

    // A phone's row holds three beside Play, like an episode's; the rest go to the top of ⋯.
    @Test func aPhoneBeforeStartingKeepsTrailerAndWatchlist() {
        let a = actions(download: true)
        #expect(a.buttons == [.play, .trailer, .watchlist, .download, .more])
        #expect(a.menu == [.button(.watched)])
    }

    @Test func aPhonePartWayKeepsStartOverAndWatched() {
        let a = actions(progress: true, download: true, subtitles: true)
        #expect(a.buttons == [.play, .startOver, .watched, .download, .more])
        #expect(a.menu == [.button(.trailer), .button(.watchlist), .subtitles])
    }

    @Test func aPhoneOnceWatchedKeepsWatchedAndWatchlist() {
        let a = actions(watched: true, download: true)
        #expect(a.buttons == [.play, .watched, .watchlist, .download, .more])
        #expect(a.menu == [.button(.trailer)])
    }

    @Test func aPhoneShowShufflesFromTheMenu() {
        let a = actions(kind: .show)
        #expect(a.buttons == [.play, .trailer, .watched, .watchlist, .more])
        #expect(a.menu == [.button(.shuffle)])
        #expect(actions(kind: .show, platform: .tv).buttons == [.play, .shuffle, .trailer, .watched, .watchlist])
    }

    @Test func aPhoneEpisodeMatchesItsPage() {
        let a = actions(kind: .episode, progress: true, trailer: false, download: true, watchlist: false)
        #expect(a.buttons == [.play, .startOver, .watched, .download, .more])
        #expect(a.menu == [.goToShow])
    }

    @Test func aPhoneWithoutATrailerFillsTheRowWithWatched() {
        #expect(actions(trailer: false, download: true).buttons == [.play, .watched, .watchlist, .download])
    }

    @Test func showsShuffle() {
        let a = actions(kind: .show, platform: .tv)
        #expect(a.buttons == [.play, .shuffle, .trailer, .watched, .watchlist])
        #expect(!actions().has(.shuffle))
        #expect(!actions(kind: .episode).has(.shuffle))
    }

    @Test func episodesGoToTheirShowFromTheMenu() {
        let a = actions(kind: .episode, trailer: false, watchlist: false)
        #expect(a.buttons == [.play, .watched, .more])
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
        #expect(actions(download: true).has(.download))
        #expect(!actions(platform: .tv, download: true).has(.download))
        #expect(!actions(download: false).has(.download))
    }

    @Test func theMenuKeepsItsOrder() {
        let a = actions(kind: .show, progress: true, subtitles: true)
        #expect(a.menu == [.button(.shuffle), .button(.trailer), .subtitles])
        #expect(actions(kind: .episode, platform: .tv, subtitles: true).menu == [.subtitles, .goToShow])
        #expect(a.buttons.last == .more)
    }
}
