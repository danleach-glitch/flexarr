import AppKit
import Observation
import SwiftUI

/// Where the Mac's player screen goes: the main window shows it over everything (in place of the page) while a title
/// plays, and takes it away when it closes. The iPhone presents the screen full screen instead.
@MainActor @Observable
final class PlayerHost {
    static let shared = PlayerHost()

    /// The screen up now, if any.
    private(set) var screen: PlayerViewController?

    func show(_ screen: PlayerViewController) {
        self.screen = screen
    }

    /// The screen has finished closing.
    func remove(_ screen: PlayerViewController) {
        if self.screen === screen { self.screen = nil }
    }
}

/// A sheet over the player (the subtitle search), closed with `dismiss`.
@MainActor
final class PlayerSheetHost {
    let controller: NSViewController
    init(controller: NSViewController) { self.controller = controller }

    func dismiss(animated: Bool) {
        controller.presentingViewController?.dismiss(controller)
    }
}

/// The player screen inside the main window's SwiftUI.
struct PlayerScreenView: NSViewControllerRepresentable {
    let screen: PlayerViewController

    func makeNSViewController(context: Context) -> PlayerViewController { screen }
    func updateNSViewController(_ controller: PlayerViewController, context: Context) {}
}
