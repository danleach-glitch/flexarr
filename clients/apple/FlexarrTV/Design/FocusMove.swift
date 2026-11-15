import UIKit

/// Which way the remote last moved focus, from UIKit's focus updates, which SwiftUI's focus state doesn't say. Tells
/// focus pressed in from the sidebar (to the right, or a row selected) from focus pressed up or across from something
/// on the screen itself.
@MainActor
enum FocusMove {
    private static var lastHeading: UIFocusHeading = []
    private static var lastAt = Date.distantPast
    private static var observer: NSObjectProtocol?
    /// When the remote last moved focus somewhere (up, down, left or right), rather than a screen placing it.
    private(set) static var lastPressedAt = Date.distantPast

    /// Starts listening; once, as the app starts.
    static func start() {
        guard observer == nil else { return }
        observer = NotificationCenter.default.addObserver(forName: UIFocusSystem.didUpdateNotification, object: nil,
                                                          queue: .main) { note in
            let heading = (note.userInfo?[UIFocusSystem.focusUpdateContextUserInfoKey] as? UIFocusUpdateContext)?.focusHeading ?? []
            MainActor.assumeIsolated {
                lastHeading = heading
                lastAt = .now
                if !heading.isEmpty { lastPressedAt = .now }
            }
        }
    }

    /// Focus hasn't been moved with the remote since `date` (a page opening): the page may still put it where it starts.
    static func untouched(since date: Date) -> Bool { lastPressedAt < date }

    /// Whether the move that just happened came from the left (the sidebar sits there) or from no direction at all (a
    /// sidebar row selected, a screen appearing), rather than up, down or leftwards from something on the screen.
    /// Nil when UIKit hasn't said in the last moment. Right to left, the sidebar is on the right.
    static func cameFromSidebar(rightToLeft: Bool = false) -> Bool? {
        guard Date.now.timeIntervalSince(lastAt) < 0.5 else { return nil }
        return lastHeading.isDisjoint(with: [.up, .down, rightToLeft ? .right : .left])
    }
}
