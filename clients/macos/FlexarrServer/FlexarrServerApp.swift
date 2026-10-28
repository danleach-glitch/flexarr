import AppKit
import SwiftUI
import SystemConfiguration

/// The Flexarr server as a menu bar app: it runs the server bundled inside it. Everything else, media folders
/// included, is set up in the web app it serves.
@main
struct FlexarrServerApp: App {
    @NSApplicationDelegateAdaptor private var delegate: AppDelegate

    var body: some Scene {
        MenuBarExtra {
            ServerMenu(server: delegate.server)
        } label: {
            // The Flexarr mark, as a template image that macOS tints for the menu bar; dimmed while the server isn't up.
            Image("MenuBarIcon")
                .opacity(delegate.server.status == .running ? 1 : 0.4)
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
        // Only when something's wrong: a running server needs no status line.
        if server.status != .running {
            Text(server.status.description)
            Divider()
        }
        Button("Open Flexarr") { NSWorkspace.shared.open(server.address) }
            .disabled(server.status != .running)
            .keyboardShortcut("o")
        // A new server's welcome page asks for this; choosing it copies it.
        if let code = server.setupCode {
            Button("Setup Code: \(code)") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(code, forType: .string)
            }
        }
        Button("Copy Address") {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(networkAddress, forType: .string)
        }
        Divider()
        Button("Show Logs") { NSWorkspace.shared.open(server.logDirectory) }
        Button("Restart Server") { server.restart() }
        Divider()
        Button("About Flexarr Server") {
            NSApp.activate()
            NSApp.orderFrontStandardAboutPanel(nil)
        }
        Button("flexarr.io") { NSWorkspace.shared.open(URL(string: "https://flexarr.io")!) }
        Divider()
        Button("Quit Flexarr") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }

    /// The address other devices on the network use: this Mac's Bonjour name, as the server announces it.
    private var networkAddress: String {
        let name = SCDynamicStoreCopyLocalHostName(nil).map { String($0) } ?? "localhost"
        return "http://\(name).local:\(server.port)"
    }
}
