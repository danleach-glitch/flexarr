import AppKit
import SwiftUI

/// The Flexarr server as a menu bar app: it runs the server bundled inside it. Everything else, media folders
/// included, is set up in the web app it serves.
@main
struct FlexarrServerApp: App {
    @NSApplicationDelegateAdaptor private var delegate: AppDelegate

    var body: some Scene {
        MenuBarExtra {
            ServerMenu(server: delegate.server)
        } label: {
            // The Flexarr mark, as a template image that macOS tints for the menu bar.
            Image("MenuBarIcon")
                .accessibilityLabel("Flexarr Server")
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let server = ServerProcess(folders: MediaFolders())

    func applicationDidFinishLaunching(_ notification: Notification) {
        server.start()
    }

    func applicationWillTerminate(_ notification: Notification) {
        server.stop()
    }
}

struct ServerMenu: View {
    let server: ServerProcess

    var body: some View {
        Button("Open Flexarr") { NSWorkspace.shared.open(server.address) }
            .disabled(server.status != .running)
            .keyboardShortcut("o")
        Button("Show Logs") { NSWorkspace.shared.open(server.logDirectory) }
        Button("Restart Server") { server.restart() }
        Divider()
        Button("Quit Flexarr") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }
}
