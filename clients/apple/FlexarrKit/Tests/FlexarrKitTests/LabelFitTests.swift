import Testing
@testable import FlexarrKit

struct LabelFitTests {
    @Test func namesThatFitStayFullSize() {
        #expect(LabelFit.scale(widths: [32, 50, 58, 48], share: 80) == 1)
    }

    @Test func longNamesShrinkToTheirShare() {
        // The widest, 86 points, in an 80-point share with 6 to spare: 74/86.
        let scale = LabelFit.scale(widths: [50, 70, 86, 72], share: 80)
        #expect(abs(scale - 74.0 / 86.0) < 0.0001)
    }

    @Test func neverBelowTheMinimum() {
        #expect(LabelFit.scale(widths: [200], share: 80) == 0.75)
    }

    @Test func nothingToMeasureIsFullSize() {
        #expect(LabelFit.scale(widths: [], share: 80) == 1)
        #expect(LabelFit.scale(widths: [60], share: 4) == 1)
    }
}
