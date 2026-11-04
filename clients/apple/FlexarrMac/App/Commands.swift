import FlexarrKit
import SwiftUI

/// The Mac's menus: Settings (⌘,), the View menu's places (⌘1…) and Back (⌘[), Find (⌘F), and the Playback menu.
/// The player's own keys (Space, the arrows, F, Esc) are its own (FlexarrMac/Player).
struct FlexarrCommands: Commands {
    let model: AppModel

    var body: some Commands {
        CommandGroup(replacing: .appSettings) {
            Button(t("Settings…")) { MacNavigation.shared.selection = .settings }
                .keyboardShortcut(",")
                .disabled(model.isKids || model.currentProfile == nil)
        }
        CommandGroup(replacing: .newItem) {}
        CommandGroup(after: .sidebar) {
            Divider()
            Button(t(Words.home)) { MacNavigation.shared.selection = .home }
                .keyboardShortcut("1")
            Button(t(Words.watchlist)) { MacNavigation.shared.selection = .watchlist }
                .keyboardShortcut("2")
                .disabled(!model.hasWatchlist)
            Button(t(Words.downloads)) { MacNavigation.shared.selection = .downloads }
                .keyboardShortcut("3")
            Divider()
            Button(t("Back")) { MacNavigation.shared.goBack() }
                .keyboardShortcut("[")
                .disabled(!MacNavigation.shared.canGoBack)
        }
        CommandGroup(after: .textEditing) {
            // The page's own search field where it has one (a library, Discover), else Search.
            Button(t(Words.search)) { MacNavigation.shared.findOnPage() }
                .keyboardShortcut("f")
        }
        CommandMenu(t("Playback")) {
            PlaybackCommands(model: model)
        }
    }
}

/// Play/Pause and skipping, for what's playing: the film or episode on the player, else music or a book. No arrow-key
/// shortcuts here: menu keys come before text fields, and ⌘← ⌘→ belong to them (the player takes the arrows itself).
private struct PlaybackCommands: View {
    let model: AppModel

    var body: some View {
        let screen = PlayerHost.shared.screen
        let music = model.music
        Button(screen?.state.isPlaying ?? music.isPlaying ? t("Pause") : t(Words.play)) {
            if let screen { screen.togglePlayPause() } else { music.isPlaying ? music.pause() : music.resume() }
        }
        .disabled(screen == nil && music.current == nil)
        Button(t("Skip forward")) {
            if let screen { screen.skip(PlayerViewController.skipSeconds, flash: true) } else { music.skip(by: Double(music.skipForward)) }
        }
        .disabled(screen == nil && music.current == nil)
        Button(t("Skip back")) {
            if let screen { screen.skip(-PlayerViewController.skipSeconds, flash: true) } else { music.skip(by: -Double(music.skipBack)) }
        }
        .disabled(screen == nil && music.current == nil)
        Divider()
        Button(t("Next")) { if let screen { screen.next() } else { music.next() } }
            .disabled(screen == nil && !music.hasNext)
        Button(t("Previous")) { if let screen { screen.previous() } else { music.previous() } }
            .disabled(screen == nil && !music.hasPrevious)
        if screen != nil {
            Divider()
            // Esc closes it, from the player itself (after leaving full screen); ⌘W stays the window's.
            Button(t("Close")) { screen?.close() }
        }
    }
}
