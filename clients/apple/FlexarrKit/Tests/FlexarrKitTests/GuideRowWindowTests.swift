import Testing
@testable import FlexarrKit

/// The iPhone, iPad and Mac guide builds only the channel rows near the screen, not all of them (audit perf-16).
struct GuideRowWindowTests {
    private let pitch = 60.0
    private let top = 42.0

    private func rows(_ offset: Double, height: Double = 800) -> Range<Int> {
        GuideRowWindow.rows(visibleTop: offset, visibleHeight: height, firstRowAt: top, pitch: pitch)
    }

    @Test func atTheTopItBuildsTheScreenAndAScreenMore() {
        let window = rows(0)
        #expect(window.lowerBound == 0)
        // 800 points is 14 rows: those and as many again below, rounded to a chunk.
        #expect(window.upperBound >= 28 && window.upperBound <= 40)
        #expect(window.count < 300)
    }

    @Test func everyRowOnScreenIsBuiltWhereverItIsScrolled() {
        for offset in stride(from: 0.0, through: 300 * pitch, by: 7.3) {
            let window = rows(offset)
            let firstOnScreen = max(0, Int(((offset - top) / pitch).rounded(.down)))
            let lastOnScreen = Int(((offset + 800 - top) / pitch).rounded(.up))
            #expect(window.lowerBound <= firstOnScreen)
            #expect(window.upperBound >= lastOnScreen)
            #expect(window.count <= 60)
        }
    }

    @Test func aSmallScrollKeepsTheSameRows() {
        #expect(rows(1200) == rows(1230))
    }

    @Test func farDownItStartsBelowTheTop() {
        #expect(rows(250 * pitch).lowerBound > 200)
    }

    @Test func nonsenseStillBuildsSomething() {
        #expect(!GuideRowWindow.rows(visibleTop: .nan, visibleHeight: 800, firstRowAt: top, pitch: pitch).isEmpty)
        #expect(!GuideRowWindow.rows(visibleTop: 0, visibleHeight: 800, firstRowAt: top, pitch: 0).isEmpty)
        #expect(rows(-500).lowerBound == 0)
    }
}
