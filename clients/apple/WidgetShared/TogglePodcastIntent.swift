import AppIntents
import Foundation

/// The Play/Pause button on the podcast widgets. An audio intent, so the system runs it in the app (which owns the
/// player), waking it if need be; the widget only shows the button.
struct TogglePodcastIntent: AudioPlaybackIntent {
    static let title: LocalizedStringResource = "Play or pause the podcast"
    static let isDiscoverable = false

    @Parameter(title: "Episode")
    var episodeId: String

    init() {}

    init(episodeId: String) {
        self.episodeId = episodeId
    }

    #if WIDGET
    func perform() async throws -> some IntentResult { .result() }
    #else
    @Dependency private var model: AppModel

    @MainActor
    func perform() async throws -> some IntentResult {
        if model.music.current?.id == episodeId {
            model.music.togglePlayPause()
        } else {
            await model.playFromIntent(itemId: episodeId)
        }
        return .result()
    }
    #endif
}
