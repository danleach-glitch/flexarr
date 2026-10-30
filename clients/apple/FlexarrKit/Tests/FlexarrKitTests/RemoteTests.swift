import Foundation
import Testing
@testable import FlexarrKit

struct RemoteTests {
    private let now = Date(timeIntervalSince1970: 10_000)

    private func device(_ id: String, type: DeviceType?, paused: Bool = false, reportedAgo: TimeInterval? = 5, online: Bool = true, thisOne: Bool = false,
                        playing: Bool = true, used: TimeInterval = 0) -> Device {
        let item = ItemSummary(id: "e-\(id)", libraryId: "", parentId: nil, kind: .episode, title: "Pilot", year: nil, seasonNumber: 1, episodeNumber: 1,
                               episodeNumberEnd: nil, airDate: nil, childCount: 0, durationSeconds: 600, resolution: nil, addedAt: now,
                               artwork: Artwork(), accentColor: nil, communityRating: nil, criticRating: nil, contentRating: nil, runtimeMinutes: nil,
                               releaseDate: nil, userData: UserData(positionSeconds: 0, watched: false, playCount: 0, lastPlayedAt: nil, unwatchedCount: nil),
                               seriesTitle: "Show")
        let nowPlaying = playing ? NowPlaying(item: item, positionSeconds: 10, durationSeconds: 600, paused: paused,
                                              reportedAt: reportedAgo.map { now.addingTimeInterval(-$0) }) : nil
        return Device(id: id, name: id, online: online, lastUsedAt: now.addingTimeInterval(-used), profileId: "owner", profileName: nil, isThisOne: thisOne,
                      nowPlaying: nowPlaying, deviceType: type)
    }

    @Test func playingTVs_areTVsThatArePlaying_lastUsedFirst() {
        let list = [
            device("lounge", type: .appleTv, used: 60),
            device("bedroom", type: .androidTv, used: 10),
            device("phone", type: .iPhone),
            device("ipad", type: .iPad),
            device("me", type: .appleTv, thisOne: true),
            device("idle", type: .appleTv, playing: false),
            device("old-server", type: nil, reportedAgo: nil, used: 100),
        ]
        #expect(list.playingTVs(at: now).map(\.id) == ["bedroom", "lounge", "old-server"])
    }

    @Test func playingTVs_dropSilentOnes_andPausedOnesThatClosedFlexarr() {
        let list = [
            device("silent", type: .appleTv, reportedAgo: 60),
            device("paused-open", type: .appleTv, paused: true, reportedAgo: 600),
            device("paused-closed", type: .appleTv, paused: true, reportedAgo: 120, online: false),
            device("paused-just-closed", type: .appleTv, paused: true, reportedAgo: 30, online: false),
        ]
        #expect(Set(list.playingTVs(at: now).map(\.id)) == ["paused-open", "paused-just-closed"])
    }

    @Test func remoteTimes_areNewsOnlyWhenTheClockWouldBeWrong() {
        let shown = RemoteTimes(paused: false, position: 100, at: 1_000, duration: 3_000, rate: 1)
        #expect(!RemoteTimes(paused: false, position: 110, at: 1_010, duration: 3_000, rate: 1).isNews(after: shown))
        #expect(RemoteTimes(paused: false, position: 300, at: 1_010, duration: 3_000, rate: 1).isNews(after: shown))
        #expect(RemoteTimes(paused: true, position: 110, at: 1_010, duration: 3_000, rate: 1).isNews(after: shown))
        #expect(RemoteTimes(paused: false, position: 110, at: 1_010, duration: 3_000, rate: 1.5).isNews(after: shown))
        #expect(shown.position(at: 4_000) == 3_000)
    }

    @Test func deviceLists_matchDespiteAMomentsDifferenceInReportTimes() {
        let a = [device("lounge", type: .appleTv, reportedAgo: 5)]
        let b = [device("lounge", type: .appleTv, reportedAgo: 5.3)]
        let c = [device("lounge", type: .appleTv, reportedAgo: 20)]
        #expect(a.matches(b))
        #expect(!a.matches(c))
        #expect(!a.matches([]))
    }

    @Test func subtitlesToTurnOn_keepWhatsOn_thenTheLanguage_neverForcedFirst() {
        let tracks = PlayerTracks(audio: [], subtitles: [
            PlayerTrack(id: "1", label: "English (Forced)", selected: false),
            PlayerTrack(id: "2", label: "French", selected: false),
            PlayerTrack(id: "3", label: "English", selected: false),
        ])
        #expect(tracks.subtitlesToTurnOn(language: "English")?.id == "3")
        #expect(tracks.subtitlesToTurnOn(language: "German")?.id == "2")
        let on = PlayerTracks(audio: [], subtitles: [PlayerTrack(id: "1", label: "French", selected: true), PlayerTrack(id: "2", label: "English", selected: false)])
        #expect(on.subtitlesToTurnOn(language: "English")?.id == "1")
        #expect(PlayerTracks(audio: [], subtitles: []).subtitlesToTurnOn(language: "English") == nil)
    }
}
