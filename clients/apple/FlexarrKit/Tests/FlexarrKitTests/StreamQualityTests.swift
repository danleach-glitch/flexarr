import Foundation
import Testing
@testable import FlexarrKit

@Suite struct StreamQualityTests {
    @Test func levelsAskForTheLaddersHeightsAndBitrates() {
        var request = PlaybackRequest(itemId: "1", profile: .iOS(hdr: false))
        StreamQuality.p720.apply(to: &request)
        #expect(request.maxHeight == 720)
        #expect(request.maxBitrate == 4_000_000)
        #expect(request.original == nil)
        StreamQuality.original.apply(to: &request)
        #expect(request.maxHeight == nil && request.maxBitrate == nil)
        #expect(request.original == true)
        StreamQuality.automatic.apply(to: &request)
        #expect(request.original == nil && request.maxHeight == nil)
        #expect(StreamQuality.p1080.bitrate == 8_000_000 && StreamQuality.p480.bitrate == 1_500_000)
    }

    @Test func stepsDownFromWhatsPlaying() {
        #expect(StreamQuality.stepDown(from: .automatic, playingHeight: 2160) == .p1080)
        #expect(StreamQuality.stepDown(from: .automatic, playingHeight: 1080) == .p720)
        #expect(StreamQuality.stepDown(from: .automatic, playingHeight: StreamQuality.effectiveHeight(width: 1920, height: 800)) == .p720)
        #expect(StreamQuality.stepDown(from: .automatic, playingHeight: 1072) == .p720)
        #expect(StreamQuality.stepDown(from: .p720, playingHeight: 1080) == .p480)
        #expect(StreamQuality.stepDown(from: .p480, playingHeight: nil) == nil)
        #expect(StreamQuality.stepDown(from: .automatic, playingHeight: 480) == nil)
        #expect(StreamQuality.stepDown(from: .automatic, playingHeight: nil) == .p720)
    }

    @Test func onlyTheLowestLevelFallsBackToAutomatic() {
        #expect(StreamQuality.fallBack(from: .p480) == .automatic)
        for quality in [StreamQuality.automatic, .original, .p1080, .p720] {
            #expect(StreamQuality.fallBack(from: quality) == nil)
        }
    }

    @Test func stepsUpTowardsTheOriginal() {
        #expect(StreamQuality.stepUp(from: .p480, originalHeight: 2160) == .p720)
        #expect(StreamQuality.stepUp(from: .p720, originalHeight: 2160) == .p1080)
        #expect(StreamQuality.stepUp(from: .p1080, originalHeight: 2160) == .automatic)
        #expect(StreamQuality.stepUp(from: .p720, originalHeight: 1080) == .automatic)
        #expect(StreamQuality.stepUp(from: .automatic, originalHeight: 1080) == nil)
    }

    @Test func fitsWithHeadroom() {
        // 8 Mbps peaks at 12, which needs 16 Mbps at three quarters.
        #expect(StreamQuality.fits(bitrate: 8_000_000, speed: 16_000_000))
        #expect(!StreamQuality.fits(bitrate: 8_000_000, speed: 15_000_000))
    }

    @Test func remembersTheAwayChoiceButStartsAtHomeOnAutomatic() throws {
        let suite = "StreamQualityTests-\(UUID().uuidString)"
        let memory = try #require(UserDefaults(suiteName: suite))
        defer { memory.removePersistentDomain(forName: suite) }
        #expect(StreamQuality.remembered(in: memory) == .automatic)
        StreamQuality.remember(.p720, in: memory)
        #expect(StreamQuality.forStart(awayFromHome: true, in: memory) == .p720)
        #expect(StreamQuality.forStart(awayFromHome: false, in: memory) == .automatic)
    }
}

@Suite struct QualityWatchTests {
    let start = Date(timeIntervalSince1970: 1_000_000)
    func at(_ seconds: TimeInterval) -> Date { start.addingTimeInterval(seconds) }

    @Test func twoStallsInAMinuteStepDown() {
        var watch = QualityWatch(at: start)
        do { let stepped = watch.stalled(at: at(20)); #expect(!stepped) }
        watch.resumed(at: at(22))
        do { let stepped = watch.stalled(at: at(50)); #expect(stepped) }
    }

    @Test func stallsAMinuteApartDont() {
        var watch = QualityWatch(at: start)
        do { let stepped = watch.stalled(at: at(20)); #expect(!stepped) }
        watch.resumed(at: at(22))
        do { let stepped = watch.stalled(at: at(85)); #expect(!stepped) }
    }

    @Test func oneLongStallStepsDown() {
        var watch = QualityWatch(at: start)
        do { let stepped = watch.stalled(at: at(30)); #expect(!stepped) }
        #expect(!watch.shouldStepDown(at: at(39)))
        #expect(watch.shouldStepDown(at: at(41)))
    }

    @Test func theFirstSecondsAfterAStartOrSeekDontCount() {
        var watch = QualityWatch(at: start)
        do { let stepped = watch.stalled(at: at(1)); #expect(!stepped) }
        watch.resumed(at: at(2))
        do { let stepped = watch.stalled(at: at(20)); #expect(!stepped) }
        watch.resumed(at: at(21))
        watch.started(at: at(30)) // a seek
        do { let stepped = watch.stalled(at: at(31)); #expect(!stepped) }
        // A stall that begins in the grace counts its length from the grace's end.
        #expect(!watch.shouldStepDown(at: at(43)))
        #expect(watch.shouldStepDown(at: at(44)))
    }

    @Test func stepsUpAfterFiveSmoothMinutesAtMostEveryFive() {
        var watch = QualityWatch(at: start)
        #expect(!watch.mayStepUp(at: at(299)))
        #expect(watch.mayStepUp(at: at(301)))
        _ = watch.stalled(at: at(310))
        #expect(!watch.mayStepUp(at: at(320)))
        watch.resumed(at: at(312))
        #expect(!watch.mayStepUp(at: at(600)))
        #expect(watch.mayStepUp(at: at(613)))
        watch.stepped(up: true, at: at(620))
        #expect(!watch.mayStepUp(at: at(900)))
        #expect(watch.mayStepUp(at: at(921)))
    }

    @Test func aStepForgetsTheStallsBefore() {
        var watch = QualityWatch(at: start)
        _ = watch.stalled(at: at(20))
        watch.stepped(up: false, at: at(25))
        watch.resumed(at: at(27))
        do { let stepped = watch.stalled(at: at(40)); #expect(!stepped) }
    }

    @Test func menuOffersOnlyLevelsBelowTheFile() {
        #expect(StreamQuality.choices(fileHeight: 720, selected: .automatic) == [.automatic, .original, .p480])
        #expect(StreamQuality.choices(fileHeight: 1080, selected: .automatic) == [.automatic, .original, .p720, .p480])
        #expect(StreamQuality.choices(fileHeight: 2160, selected: .automatic) == [.automatic, .original, .p1080, .p720, .p480])
        #expect(StreamQuality.choices(fileHeight: nil, selected: .automatic) == [.automatic, .original, .p1080, .p720, .p480])
        #expect(StreamQuality.choices(fileHeight: 480, selected: .automatic) == [.automatic, .original])
        // A level remembered from another title stays, ticked.
        #expect(StreamQuality.choices(fileHeight: 720, selected: .p1080) == [.automatic, .original, .p1080, .p480])
    }

    @Test func namesHeights() {
        #expect(StreamQuality.label(forHeight: 2160) == "4K")
        #expect(StreamQuality.label(forHeight: 1072) == "1080p")
        #expect(StreamQuality.label(forHeight: 720) == "720p")
        #expect(StreamQuality.label(forHeight: 540) == "540p")
        #expect(StreamQuality.height(resolutionLabel: "1080p") == 1080)
        #expect(StreamQuality.height(resolutionLabel: nil) == nil)
    }

    @Test func menuSaysWhatsPlaying() {
        let automatic = QualityMenu(selected: .automatic, fileHeight: 2160, playing: "720p", file: "4K HDR")
        #expect(automatic.badge == nil)
        #expect(automatic.detail(for: .automatic) == "720p")
        #expect(automatic.detail(for: .original) == "4K HDR")
        #expect(automatic.detail(for: .p720) == nil)
        #expect(automatic.spokenValue == "Automatic, 720p")
        let chosen = QualityMenu(selected: .p720, fileHeight: 1080, playing: "720p", file: "1080p")
        #expect(chosen.badge == "720p")
        #expect(chosen.spokenValue == "720p")
    }
}
