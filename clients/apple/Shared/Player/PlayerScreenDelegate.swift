import FlexarrKit
import Foundation

/// What the player screen tells the controller. The screen is UIKit's on the iPhone and Apple TV (Shared/Player)
/// and AppKit's on the Mac (FlexarrMac/Player); both are called PlayerViewController and show the same things.
@MainActor
protocol PlayerScreenDelegate: AnyObject {
    func playerScreen(_ screen: PlayerViewController, phaseChanged phase: TransportPhase)
    func playerScreen(_ screen: PlayerViewController, tick seconds: Double)
    func playerScreenTracksChanged(_ screen: PlayerViewController)
    /// The viewer moved: the timeline, a skip, the lock screen.
    func playerScreen(_ screen: PlayerViewController, didSeekTo seconds: Double)
    /// The viewer closed the player (or Picture in Picture ended without coming back).
    func playerScreenDidClose(_ screen: PlayerViewController)
    func playerScreenPrevious(_ screen: PlayerViewController)
    func playerScreenNext(_ screen: PlayerViewController)
    func playerScreenPromptTapped(_ screen: PlayerViewController)
    func playerScreenCancelNext(_ screen: PlayerViewController)
    /// Up on a TV during a countdown (Skip intro, Next Episode): stop it, leaving the button focused.
    func playerScreenHoldCountdown(_ screen: PlayerViewController)
    /// An audio track the transport can't switch to itself: the session is started again with it.
    func playerScreen(_ screen: PlayerViewController, selectAudio id: String)
    /// The viewer chose subtitles (`.some(nil)`: off), to remember for this title and the next.
    func playerScreen(_ screen: PlayerViewController, choseSubtitle key: String??)
    func playerScreen(_ screen: PlayerViewController, subtitleDelay seconds: Double)
    func playerScreen(_ screen: PlayerViewController, subtitleSize size: SubtitleSize)
    func playerScreen(_ screen: PlayerViewController, subtitlePlacement placement: SubtitlePlacement)
    /// The viewer nudged the file's sound (seconds later, earlier when negative).
    func playerScreen(_ screen: PlayerViewController, audioDelay seconds: Double)
    /// The viewer chose a speed in the menu.
    func playerScreen(_ screen: PlayerViewController, choseRate rate: Float)
    /// The sleep timer was set, changed, asked, ran out or was woken from (`sleepHoldsEpisode` may have changed).
    func playerScreenSleepChanged(_ screen: PlayerViewController)
    /// "Search for subtitles…" in the subtitles menu.
    func playerScreenSearchSubtitles(_ screen: PlayerViewController)
    /// "Re-sync to audio" for the subtitle file showing (its server key).
    func playerScreen(_ screen: PlayerViewController, resyncSubtitle key: String)
}

extension PlayerScreenDelegate {
    func playerScreenHoldCountdown(_ screen: PlayerViewController) {}
    func playerScreen(_ screen: PlayerViewController, audioDelay seconds: Double) {}
    func playerScreen(_ screen: PlayerViewController, choseRate rate: Float) {}
    func playerScreenSleepChanged(_ screen: PlayerViewController) {}
    func playerScreenSearchSubtitles(_ screen: PlayerViewController) {}
    func playerScreen(_ screen: PlayerViewController, resyncSubtitle key: String) {}
}
