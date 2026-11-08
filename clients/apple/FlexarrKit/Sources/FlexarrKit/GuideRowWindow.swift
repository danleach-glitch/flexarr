import Foundation

/// Which rows of a long, scrolled list of equal rows to build: those on screen and about a screen's worth either side,
/// in steps of `chunk` rows so a scroll changes the answer only now and then (audit perf-16). The iPhone, iPad and Mac
/// guide builds just these, so a few hundred channels don't lay out thousands of programmes at once.
public enum GuideRowWindow {
    /// - Parameters:
    ///   - visibleTop: the top of what's on screen, in the scrolled content's coordinates.
    ///   - visibleHeight: how tall what's on screen is.
    ///   - firstRowAt: where the first row starts in the content.
    ///   - pitch: a row's height plus the gap below it.
    /// - Returns: the rows to build; never below 0. The caller limits it to the rows it has.
    public static func rows(visibleTop: Double, visibleHeight: Double, firstRowAt: Double, pitch: Double,
                            chunk: Int = 10) -> Range<Int> {
        guard pitch > 0, chunk > 0, visibleTop.isFinite, visibleHeight.isFinite else { return 0..<chunk }
        let onScreen = max(1, Int((visibleHeight / pitch).rounded(.up)))
        let margin = max(chunk, onScreen)
        let first = Int(((visibleTop - firstRowAt) / pitch).rounded(.down))
        let last = Int(((visibleTop + visibleHeight - firstRowAt) / pitch).rounded(.up))
        let lower = max(0, floor(first - margin, to: chunk))
        let upper = max(lower + chunk, ceil(last + margin, to: chunk))
        return lower..<upper
    }

    private static func floor(_ value: Int, to step: Int) -> Int {
        value >= 0 ? value / step * step : -((-value + step - 1) / step * step)
    }

    private static func ceil(_ value: Int, to step: Int) -> Int {
        value >= 0 ? (value + step - 1) / step * step : -(-value / step * step)
    }
}
