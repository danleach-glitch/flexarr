import Foundation
import Testing
@testable import FlexarrKit

struct BookTimelineTests {
    /// Three files: 0–100, 100–220, 220–300.
    private let timeline = BookTimeline(
        files: [BookFile(index: 0, fileId: "a", offsetSeconds: 0, durationSeconds: 100),
                BookFile(index: 1, fileId: "b", offsetSeconds: 100, durationSeconds: 120),
                BookFile(index: 2, fileId: "c", offsetSeconds: 220, durationSeconds: 80)],
        chapters: [BookChapter(index: 0, title: "One", startSeconds: 0, endSeconds: 60),
                   BookChapter(index: 1, title: "Two", startSeconds: 60, endSeconds: 220),
                   BookChapter(index: 2, title: "Three", startSeconds: 220, endSeconds: 300)])

    @Test func placesMapToAFileAndAnOffset() {
        #expect(timeline.duration == 300)
        #expect(timeline.locate(0) == (0, 0))
        #expect(timeline.locate(50) == (0, 50))
        #expect(timeline.locate(150) == (1, 50))
        #expect(timeline.locate(299) == (2, 79))
    }

    @Test func aFileBoundaryBelongsToTheFileThatStartsThere() {
        #expect(timeline.locate(100) == (1, 0))
        #expect(timeline.locate(99.999).file == 0)
        #expect(timeline.locate(220) == (2, 0))
    }

    @Test func beyondTheEndsStaysInTheBook() {
        #expect(timeline.locate(-5) == (0, 0))
        #expect(timeline.locate(500) == (2, 80))
        #expect(timeline.locate(.nan) == (0, 0))
        #expect(BookTimeline(files: []).locate(10) == (0, 0))
    }

    @Test func fileOffsetsMapBackToTheTimeline() {
        #expect(timeline.position(file: 1, offset: 20) == 120)
        #expect(timeline.position(file: 2, offset: 500) == 300)
        #expect(timeline.position(file: 7, offset: 1) == 0)
        for place in [0.0, 99.5, 100, 180, 220, 299] {
            let (file, offset) = timeline.locate(place)
            #expect(timeline.position(file: file, offset: offset) == place)
        }
    }

    @Test func filesListedOutOfOrderAreSorted() {
        let shuffled = BookTimeline(files: [BookFile(index: 1, fileId: "b", offsetSeconds: 10, durationSeconds: 10),
                                            BookFile(index: 0, fileId: "a", offsetSeconds: 0, durationSeconds: 10)])
        #expect(shuffled.locate(12) == (1, 2))
        #expect(shuffled.duration == 20)
    }

    @Test func chaptersAndTheirSteps() {
        #expect(timeline.chapter(at: 59)?.title == "One")
        #expect(timeline.chapter(at: 60)?.title == "Two")
        #expect(timeline.chapterEnd(at: 100) == 220)
        #expect(timeline.nextChapterStart(from: 10) == 60)
        #expect(timeline.nextChapterStart(from: 250) == nil)
        // Back to this chapter's start, or the one before when it's only just begun.
        #expect(timeline.previousChapterStart(from: 100) == 60)
        #expect(timeline.previousChapterStart(from: 61) == 0)
        #expect(timeline.previousChapterStart(from: 1) == 0)
        #expect(BookTimeline(files: []).chapter(at: 5) == nil)
    }

    @Test func aChapterWithoutAnEndEndsWhereTheNextStarts() {
        let open = BookTimeline(files: timeline.files,
                                chapters: [BookChapter(index: 0, title: "One", startSeconds: 0, endSeconds: 0),
                                           BookChapter(index: 1, title: "Two", startSeconds: 60, endSeconds: 0)])
        #expect(open.chapterEnd(at: 10) == 60)
        #expect(open.chapterEnd(at: 100) == 300)
        #expect(BookTimeline(files: timeline.files).chapterEnd(at: 10) == 300)
    }
}

struct BookListeningTests {
    @Test func smartRewindStaysInTheChapterUnlessPausedAtItsStart() {
        #expect(SmartRewind.place(from: 100, back: 0, chapterStart: 60) == 100)
        #expect(SmartRewind.place(from: 100, back: 10, chapterStart: 60) == 90)
        #expect(SmartRewind.place(from: 65, back: 10, chapterStart: 60) == 60)
        // Within a second of the chapter's start: back into the one before.
        #expect(SmartRewind.place(from: 60.5, back: 10, chapterStart: 60) == 50.5)
        #expect(SmartRewind.place(from: 5, back: 10, chapterStart: nil) == 0)
    }

    @Test func aPlacePickedWhilePausedPlaysWithoutRewind() {
        let start = Date(timeIntervalSince1970: 1000)
        var pause = BookPause()
        #expect(pause.resumed(at: start) == nil)
        pause.paused(at: start)
        pause.paused(at: start.addingTimeInterval(5))
        #expect(pause.resumed(at: start.addingTimeInterval(30)) == 30)
        #expect(pause.since == nil)
        pause.paused(at: start)
        pause.seeked()
        #expect(pause.resumed(at: start.addingTimeInterval(30)) == nil)
    }

    @Test func smartRewindGrowsWithThePause() {
        #expect(SmartRewind.seconds(afterPause: 5) == 0)
        #expect(SmartRewind.seconds(afterPause: 10) == 3)
        let minute = SmartRewind.seconds(afterPause: 60)
        let tenMinutes = SmartRewind.seconds(afterPause: 600)
        #expect(minute > 3 && minute < tenMinutes)
        #expect(SmartRewind.seconds(afterPause: 3600) == 30)
        #expect(SmartRewind.seconds(afterPause: 86_400) == 30)
    }

    @Test func speedsStayInRangeAndOnSteps() {
        #expect(BookSpeed.clamped(0.1) == 0.5)
        #expect(BookSpeed.clamped(9) == 3.5)
        #expect(abs(BookSpeed.clamped(1.27) - 1.25) < 0.0001)
        #expect(BookSpeed.clamped(.infinity) == 1)
        #expect(BookSpeed.label(1) == "1×")
        #expect(BookSpeed.label(1.25) == "1.25×")
        #expect(BookSpeed.label(1.5) == "1.5×")
    }

    @Test func jumpIsOfferedOnlyForANewerPlaceFromAnotherDevice() {
        let now = Date()
        let elsewhere = BookProgress(positionSeconds: 1000, finished: false, updatedAt: now, device: "iPad")
        #expect(BookResume.offersJump(server: elsewhere, local: 100, localAt: now.addingTimeInterval(-60), device: "iPhone"))
        #expect(!BookResume.offersJump(server: elsewhere, local: 100, localAt: now.addingTimeInterval(60), device: "iPhone"))
        #expect(!BookResume.offersJump(server: elsewhere, local: 100, localAt: nil, device: "iPad"))
        #expect(!BookResume.offersJump(server: elsewhere, local: 990, localAt: nil, device: "iPhone"))
        let finished = BookProgress(positionSeconds: 0, finished: true, updatedAt: now, device: "iPad")
        #expect(!BookResume.offersJump(server: finished, local: 100, localAt: nil, device: "iPhone"))
    }

    @Test func seriesPlaces() throws {
        let json = ##"[{"id":"s","name":"A","sequence":"3","bookCount":7},{"id":"s","name":"A","sequence":"1.5","bookCount":7},{"id":"s","name":"A","sequence":null,"bookCount":2},{"id":"s","name":"A","sequence":"0","bookCount":1}]"##
        let refs = try FlexarrClient.decoder.decode([BookSeriesRef].self, from: Data(json.utf8))
        #expect(refs.map(\.placeLabel) == ["Book 3 of 7", "Book 1.5 of 7", nil, "Book 0"])
    }
}

struct UnknownTypeTests {
    @Test func olderServersSendNoBookFields() throws {
        let cut = try #require(BookJSON.summary.range(of: #""seriesTitle":null,"#))
        let older = BookJSON.summary[..<cut.lowerBound] + #""seriesTitle":null}"#
        #expect(!older.contains("\"book\""))
        let item = try FlexarrClient.decoder.decode(ItemSummary.self, from: Data(older.utf8))
        #expect(item.book == nil)
        #expect(item.bookAuthors == nil)
        let playback = ##"{"sessionId":"s","method":"DirectPlay","url":"/x","reasons":[],"durationSeconds":1,"startPositionSeconds":0,"fileId":"f","audioTracks":[],"selectedAudioIndex":null,"subtitles":[],"selectedSubtitleKey":null,"subtitleBurnedIn":false,"markers":[],"item":\##(older),"ancestors":[],"next":null,"transcodeSummary":null,"shuffle":null,"preferences":null}"##
        #expect(try FlexarrClient.decoder.decode(PlaybackInfo.self, from: Data(playback.utf8)).book == nil)
        let person = ##"{"id":"p","name":"A","image":null,"credits":[]}"##
        #expect(try FlexarrClient.decoder.decode(Person.self, from: Data(person.utf8)).biography == nil)
    }

    @Test func libraryTypesAndKindsFromNewerServersAreUnknown() throws {
        let json = ##"[{"id":"a","name":"Books","type":"Audiobooks","folders":[],"itemCount":1,"lastScannedAt":null,"scan":null},{"id":"b","name":"Comics","type":"Comics","folders":[],"itemCount":1,"lastScannedAt":null,"scan":null}]"##
        let libraries = try FlexarrClient.decoder.decode([Library].self, from: Data(json.utf8))
        #expect(libraries.map(\.type) == [.audiobooks, .unknown])
        #expect(try FlexarrClient.decoder.decode([MediaKind].self, from: Data(#"["Audiobook","Comic"]"#.utf8)) == [.audiobook, .unknown])
        #expect(try FlexarrClient.decoder.decode([CreditRole].self, from: Data(#"["Author","Narrator","Translator","Colorist"]"#.utf8)) == [.author, .narrator, .translator, .unknown])
        #expect(try FlexarrClient.decoder.decode(ChapterSource.self, from: Data(#""Audible""#.utf8)) == .unknown)
    }

    @Test func sleepCommandsAndChapters() throws {
        let json = ##"{"id":"c","kind":"Sleep","itemId":null,"fromBeginning":false,"sentBy":"Web","sentAt":"2026-10-31T10:00:00Z","endOfChapter":true}"##
        let command = try FlexarrClient.decoder.decode(DeviceCommand.self, from: Data(json.utf8))
        #expect(command.kind == .sleep)
        #expect(command.endOfChapter == true)
        let older = ##"{"id":"c","kind":"Pause","itemId":null,"fromBeginning":false,"sentBy":null,"sentAt":"2026-10-31T10:00:00Z"}"##
        #expect(try FlexarrClient.decoder.decode(DeviceCommand.self, from: Data(older.utf8)).endOfChapter == nil)
        let playing = ##"{"item":\##(BookJSON.summary),"positionSeconds":10,"durationSeconds":300,"paused":false,"chapter":"Two"}"##
        #expect(try FlexarrClient.decoder.decode(NowPlaying.self, from: Data(playing.utf8)).chapter == "Two")
    }

    @Test func anEmptyHomeCountsAudiobooks() throws {
        let json = ##"[{"id":"a","name":"Books","type":"Audiobooks","folders":["/b"],"itemCount":0,"lastScannedAt":null,"scan":null}]"##
        let message = EmptyHomeMessage(libraries: try FlexarrClient.decoder.decode([Library].self, from: Data(json.utf8)))
        #expect(message?.title == "Your library is empty")
    }
}
