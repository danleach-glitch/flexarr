import Foundation
import Testing
@testable import FlexarrKit

struct LiveTvTests {
    @Test func channels() throws {
        let channels = try Fixture.decode([LiveChannel].self, "live-channels")
        let bbc = try #require(channels.first)
        #expect(bbc.numberText == "101")
        #expect(bbc.logoUrl?.hasPrefix("/api/v1/livetv/channels/") == true)
        let now = try #require(bbc.now)
        #expect(now.end > now.start)
        #expect(bbc.next?.start == now.end)
        #expect(bbc.with(favourite: !bbc.isFavourite).isFavourite != bbc.isFavourite)
        #expect(bbc.groupName == "UK GENERAL")
    }

    @Test func programmeProgressAndTitle() {
        let start = Date(timeIntervalSince1970: 0)
        let programme = LiveProgramme(id: 1, start: start, end: start.addingTimeInterval(3600), title: "Doctor Who", subtitle: "The Star Beast",
                                      description: nil, category: nil, season: 14, episode: 1, isNew: true, recording: nil, series: nil)
        #expect(programme.progress(at: start.addingTimeInterval(900)) == 0.25)
        #expect(programme.progress(at: start.addingTimeInterval(-60)) == 0)
        #expect(programme.displayTitle == "Doctor Who: The Star Beast")
    }

    @Test func guide() throws {
        let guide = try Fixture.decode(LiveGuide.self, "live-guide")
        #expect(guide.until > guide.from)
        let first = try #require(guide.channels.first)
        #expect(!first.programmes.isEmpty)
        let now = try #require(first.programmes.first).start.addingTimeInterval(60)
        #expect(first.channel(at: now).now == first.programmes.first)
    }

    @Test func recordings() throws {
        let list = try Fixture.decode(Recordings.self, "recordings")
        #expect(list.streams == 1)
        let recorded = try #require(list.recent.first)
        #expect(recorded.state == "completed")
        #expect(recorded.itemId != nil)
        #expect(!recorded.isRecordingNow)
    }

    @Test func aRecordingInTheWayIsReadFromTheProblem() throws {
        let problem = Data("""
            {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.10","title":"Recording","status":409,
             "detail":"F1 on Channel 4 is recording, using every stream there is. Watch Channel 4, or stop the recording.",
             "recordingId":"01a177e6-a32a-7d1f-96e9-31ebd464d4d5","channelId":"01a17633-26dc-7bc2-b4f9-1f8dd37605ed",
             "channelName":"Channel 4","recordingTitle":"F1","until":"2026-10-26T11:15:00Z"}
            """.utf8)
        let recording = try #require(RecordingInTheWay(problem: problem))
        #expect(recording.channelName == "Channel 4")
        #expect(FlexarrClientError.recordingInTheWay(recording).errorDescription?.hasPrefix("F1 on Channel 4") == true)
        #expect(RecordingInTheWay(problem: Data(#"{"detail":"Busy"}"#.utf8)) == nil)
    }
}
