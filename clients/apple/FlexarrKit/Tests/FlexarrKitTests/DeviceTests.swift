import Foundation
import Testing
@testable import FlexarrKit

struct DeviceTests {
    @Test func serverSentEvents_splitsEventsAndSkipsComments() {
        var events = ServerSentEvents()
        #expect(events.feed(": connected") == nil)
        #expect(events.feed("") == nil)
        #expect(events.feed("event: command") == nil)
        #expect(events.feed("data: {\"a\":1}") == nil)
        let first = events.feed("")
        #expect(first == ServerSentEvents.Event(name: "command", data: "{\"a\":1}"))
        // The next event starts clean, with the default name.
        #expect(events.feed("data: one") == nil)
        #expect(events.feed("data: two") == nil)
        #expect(events.feed("") == ServerSentEvents.Event(name: "message", data: "one\ntwo"))
    }

    @Test func deviceCommand_decodes() throws {
        let json = """
        {"id":"ab12","kind":"Play","itemId":"019a4c1e-0d8a-7a2e-9f1b-2c3d4e5f6a7b","fromBeginning":true,"sentBy":"Dan's iPhone","sentAt":"2026-10-25T10:00:00Z"}
        """
        let command = try FlexarrClient.decoder.decode(DeviceCommand.self, from: Data(json.utf8))
        #expect(command.kind == .play)
        #expect(command.itemId == "019a4c1e-0d8a-7a2e-9f1b-2c3d4e5f6a7b")
        #expect(command.fromBeginning)
        #expect(command.sentBy == "Dan's iPhone")
        #expect(command.profileId == nil) // whoever is watching there; servers before 0.54 never send it
        let asSam = try FlexarrClient.decoder.decode(DeviceCommand.self, from: Data(json.replacingOccurrences(of: "\"fromBeginning\"", with: "\"profileId\":\"sam\",\"fromBeginning\"").utf8))
        #expect(asSam.profileId == "sam")
        // A kind this app doesn't know yet is ignored rather than failing the whole stream.
        let newer = try FlexarrClient.decoder.decode(DeviceCommand.self, from: Data(json.replacingOccurrences(of: "\"Play\"", with: "\"Rewind\"").utf8))
        #expect(newer.kind == .unknown)
    }

    @Test func soundCommand_decodes() throws {
        let json = """
        {"id":"cd34","kind":"Sound","itemId":null,"fromBeginning":false,"sentBy":"Home Assistant","sentAt":"2026-10-30T22:00:00Z","sound":{"nightMode":"On","dialogueBoost":null,"speakers":null}}
        """
        let command = try FlexarrClient.decoder.decode(DeviceCommand.self, from: Data(json.utf8))
        #expect(command.kind == .sound)
        #expect(command.sound?.nightMode == "On")
        #expect(command.sound?.dialogueBoost == nil)
    }

    @Test func device_decodes() throws {
        let json = """
        [{"id":"d1","name":"Living room","online":true,"lastUsedAt":"2026-10-25T10:00:00Z","profileId":"owner","profileName":"Dan","isThisOne":false,"nowPlaying":null}]
        """
        let devices = try FlexarrClient.decoder.decode([Device].self, from: Data(json.utf8))
        #expect(devices.count == 1)
        #expect(devices[0].name == "Living room")
        #expect(devices[0].online)
        #expect(devices[0].nowPlaying == nil)
    }

    @Test func remoteCommand_decodes() throws {
        let json = """
        {"id":"ef56","kind":"Seek","itemId":null,"fromBeginning":false,"sentBy":"Dan's iPhone","sentAt":"2026-10-30T22:00:00Z","seconds":754.5}
        """
        let command = try FlexarrClient.decoder.decode(DeviceCommand.self, from: Data(json.utf8))
        #expect(command.kind == .seek)
        #expect(command.seconds == 754.5)
        let track = try FlexarrClient.decoder.decode(DeviceCommand.self, from: Data(json.replacingOccurrences(of: "\"Seek\"", with: "\"AudioTrack\"")
            .replacingOccurrences(of: "\"seconds\":754.5", with: "\"trackId\":\"2\"").utf8))
        #expect(track.kind == .audioTrack)
        #expect(track.trackId == "2")
    }

    @Test func deviceForARemote_decodes_andSaysWhatItTakes() throws {
        let json = """
        [{"id":"d1","name":"Living room","online":true,"lastUsedAt":"2026-10-25T10:00:00Z","profileId":"owner","profileName":"Dan","isThisOne":false,
          "nowPlaying":null,"deviceType":"AppleTv","accepts":["Pause","Play","Seek","Skip","Teleport"]},
         {"id":"d2","name":"Old TV","online":true,"lastUsedAt":"2026-10-25T10:00:00Z","profileId":"owner","profileName":"Dan","isThisOne":false,"nowPlaying":null}]
        """
        let devices = try FlexarrClient.decoder.decode([Device].self, from: Data(json.utf8))
        #expect(devices[0].deviceType == .appleTv)
        #expect(devices[0].takes(.seek))
        #expect(!devices[0].takes(.next))
        // An app that doesn't say takes only the basic ones.
        #expect(devices[1].accepts == nil)
        #expect(devices[1].takes(.pause))
        #expect(!devices[1].takes(.seek))
    }

    @Test func nowPlaying_movesOnWhilePlaying() {
        let reported = Date(timeIntervalSince1970: 1_000)
        let item = ItemSummary(id: "e1", libraryId: "", parentId: nil, kind: .episode, title: "Pilot", year: nil, seasonNumber: 1, episodeNumber: 1,
                               episodeNumberEnd: nil, airDate: nil, childCount: 0, durationSeconds: 600, resolution: nil, addedAt: reported,
                               artwork: Artwork(), accentColor: nil, communityRating: nil, criticRating: nil, contentRating: nil, runtimeMinutes: nil,
                               releaseDate: nil, userData: UserData(positionSeconds: 0, watched: false, playCount: 0, lastPlayedAt: nil, unwatchedCount: nil),
                               seriesTitle: "Show")
        let playing = NowPlaying(item: item, positionSeconds: 100, durationSeconds: 600, paused: false, reportedAt: reported, rate: 1.5)
        #expect(playing.position(at: reported.addingTimeInterval(10)) == 115)
        #expect(playing.position(at: reported.addingTimeInterval(1_000)) == 600)
        let paused = NowPlaying(item: item, positionSeconds: 100, durationSeconds: 600, paused: true, reportedAt: reported)
        #expect(paused.position(at: reported.addingTimeInterval(10)) == 100)
    }

    @Test func nowPlaying_readsDetailsLeniently_andTimesFromTheServersAge() throws {
        let json = """
        [{"id":"d1","name":"Living room","online":true,"lastUsedAt":"2026-10-25T10:00:00Z","profileId":"owner","profileName":"Dan","isThisOne":false,
          "deviceType":"AppleTv",
          "nowPlaying":{"item":{"id":"e1","libraryId":"l","parentId":null,"kind":"Episode","title":"Pilot","year":null,"seasonNumber":1,"episodeNumber":1,
            "episodeNumberEnd":null,"airDate":null,"childCount":0,"durationSeconds":600,"resolution":null,"addedAt":"2026-10-25T10:00:00Z",
            "artwork":{"poster":null,"backdrop":null,"thumb":null,"logo":null},"accentColor":null,"communityRating":null,"criticRating":null,
            "contentRating":null,"runtimeMinutes":null,"releaseDate":null,"userData":{"positionSeconds":0,"watched":false,"playCount":0,"lastPlayedAt":null},
            "seriesTitle":"Show"},
           "positionSeconds":100,"durationSeconds":600,"paused":false,"reportedAt":"2000-01-01T00:00:00Z","ageSeconds":2,"rate":1,
           "prompt":"SkipCredits","tracks":{"audio":[{"id":"1","label":null,"selected":true}],"subtitles":[]}}}]
        """
        let devices = try FlexarrClient.decoder.decode([Device].self, from: Data(json.utf8))
        let playing = try #require(devices[0].nowPlaying)
        #expect(playing.tracks == nil) // a track with no label: left out, not the whole list
        #expect(playing.prompt == .unknown)
        // From the server's age, not its (badly set) clock.
        #expect(abs(playing.reportedAt!.timeIntervalSinceNow + 2) < 1)
        #expect(devices[0].isTV)
    }
}
