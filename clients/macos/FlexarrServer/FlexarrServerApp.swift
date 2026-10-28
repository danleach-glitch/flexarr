import AppKit
import SwiftUI

/// The Flexarr server as a menu bar app: it runs the server bundled inside it and lends it the media folders
/// chosen here. Everything else is set up in the web app it serves.
@main
struct FlexarrServerApp: App {
    @NSApplicationDelegateAdaptor private var delegate: AppDelegate

    var body: some Scene {
        MenuBarExtra("Flexarr Server", systemImage: "play.rectangle.on.rectangle") {
            ServerMenu(server: delegate.server, folders: delegate.folders)
        }
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    let folders = MediaFolders()
    lazy var server = ServerProcess(folders: folders)

    func applicationDidFinishLaunching(_ notification: Notification) {
        server.start()
    }

    func applicationWillTerminate(_ notification: Notification) {
        server.stop()
    }
}

struct ServerMenu: View {
    let server: ServerProcess
    let folders: MediaFolders

    var body: some View {
        Text(server.status.description)
        Button("Open Flexarr") { NSWorkspace.shared.open(server.address) }
            .disabled(server.status != .running)
            .keyboardShortcut("o")
        Divider()
        Section("Media folders") {
            if folders.urls.isEmpty {
                Text("None yet")
            }
            ForEach(folders.urls, id: \.self) { url in
                Menu(url.path) {
                    Button("Copy path") {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(url.path, forType: .string)
                    }
                    Button("Remove") {
                        folders.remove(url)
                        server.restart()
                    }
                }
            }
            Button("Add a folder…") {
                if folders.choose() { server.restart() }
            }
        }
        Divider()
        Button("Show logs") { NSWorkspace.shared.open(server.logDirectory) }
        Button("Restart server") { server.restart() }
        Button("Quit Flexarr Server") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
    }
}
