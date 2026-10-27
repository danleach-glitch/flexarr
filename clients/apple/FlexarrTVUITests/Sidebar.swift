import XCTest

/// Getting around the sidebar with the Siri Remote. Its rows don't report focus to UI tests themselves, but the
/// focused cell holds the row's name, so a test can tell which row it's on before pressing Select.
@MainActor
extension XCUIApplication {
    /// The name of the sidebar row with focus ("Home", "Movies", "Settings", or the profile's name), or nil when focus
    /// is on the screen.
    var focusedSidebarRow: String? {
        let focused = descendants(matching: .any).matching(NSPredicate(format: "hasFocus == true")).firstMatch
        guard focused.exists, focused.elementType == .cell, focused.frame.minX < 10 else { return nil }
        return focused.staticTexts.allElementsBoundByIndex.first?.label
    }

    /// Brings the sidebar up (left from the screen, which also opens it from a pushed screen) and moves to the row
    /// named. Moving through the sidebar doesn't change the screen; Select does (see `openSidebarRow`).
    @discardableResult
    func focusSidebarRow(_ name: String, file: StaticString = #filePath, line: UInt = #line) -> Bool {
        let remote = XCUIRemote.shared
        for _ in 0..<10 where focusedSidebarRow == nil {
            remote.press(.left)
            Thread.sleep(forTimeInterval: 0.8)
        }
        guard focusedSidebarRow != nil else {
            XCTFail("couldn't reach the sidebar", file: file, line: line)
            return false
        }
        // Up or down towards the row, turning round at an end (where focus stops moving).
        let target = buttons[name].firstMatch
        let focusedY = { self.descendants(matching: .any).matching(NSPredicate(format: "hasFocus == true")).firstMatch.frame.minY }
        var direction: XCUIRemote.Button = target.exists && target.frame.minY < focusedY() ? .up : .down
        for _ in 0..<30 {
            guard let row = focusedSidebarRow, row != name else { break }
            remote.press(direction)
            Thread.sleep(forTimeInterval: 0.5)
            if focusedSidebarRow == row { direction = direction == .down ? .up : .down }
        }
        guard focusedSidebarRow == name else {
            XCTFail("no \(name) in the sidebar (on \(focusedSidebarRow ?? "nothing"))", file: file, line: line)
            return false
        }
        return true
    }

    /// Moves to the sidebar row named and selects it, which shows its screen. Select is pressed only once that row is
    /// known to have focus.
    @discardableResult
    func openSidebarRow(_ name: String, wait: TimeInterval = 4, file: StaticString = #filePath, line: UInt = #line) -> Bool {
        guard focusSidebarRow(name, file: file, line: line) else { return false }
        XCUIRemote.shared.press(.select)
        Thread.sleep(forTimeInterval: wait)
        return true
    }
}
