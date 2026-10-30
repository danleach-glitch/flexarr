import AVFoundation
import AVKit
import FlexarrKit
import UIKit

/// What the picture is doing, as the player shows it: a spinner while loading or buffering, the play/pause button's
/// state, the end of the title, or why it stopped.
enum TransportPhase: Equatable {
    case loading
    case buffering
    case playing
    case paused
    case ended
    case failed(String)
}

/// An audio or subtitle track as the player's menus list it. `key` is what the server knows it by (a subtitle's
/// "e3" or "x0", an audio stream's index), so the viewer's choice can be remembered and carried to the next episode.
struct TransportTrack: Identifiable, Equatable {
    let id: String
    let label: String
    let key: String?
    /// A bitmap subtitle (PGS, DVB, DVD), which can't be moved in time.
    var isPicture = false
}

@MainActor
protocol PlayerTransportDelegate: AnyObject {
    func transport(_ transport: PlayerTransport, phaseChanged phase: TransportPhase)
    /// The clock, a few times a second while playing, and once a seek lands.
    func transport(_ transport: PlayerTransport, tick seconds: Double)
    func transportTracksChanged(_ transport: PlayerTransport)
}

/// One way of getting a title onto the screen: Apple's player over a server stream or a downloaded file, or the
/// engine over the file itself. The player screen drives it and draws the controls; the controller watches it.
@MainActor
protocol PlayerTransport: AnyObject {
    var delegate: PlayerTransportDelegate? { get set }
    /// The video (and whatever the transport draws over it, such as subtitles), to fill the screen.
    var view: UIView { get }
    var phase: TransportPhase { get }
    var currentTime: Double { get }
    var duration: Double { get }
    /// How far ahead of `currentTime` the picture is loaded.
    var bufferedTime: Double { get }
    var rate: Float { get }
    var maxRate: Float { get }
    /// The picture's size in pixels, once known (for placing things over it).
    var videoSize: CGSize { get }

    func play()
    func pause()
    func seek(to seconds: Double)
    func setRate(_ rate: Float)
    func stop()
    /// The sound's level, 0 to 1 (the sleep timer fades it out).
    var volume: Float { get set }

    var audioTracks: [TransportTrack] { get }
    var activeAudioTrack: String? { get }
    /// Switches audio in place. False when this transport can't (the server chose the track): the session is
    /// started again with the new one.
    @discardableResult func selectAudioTrack(_ id: String) -> Bool
    var subtitleTracks: [TransportTrack] { get }
    var activeSubtitleTrack: String? { get }
    func selectSubtitleTrack(_ id: String?)
    var subtitleSize: SubtitleSize { get set }
    var subtitlePlacement: SubtitlePlacement { get set }

    /// Whether this device's sound settings (dialogue boost, night mode, speakers) reach the sound.
    var soundReach: SoundReach { get }
    /// Apple's player underneath, when there is one: AirPlay and Picture in Picture go through it.
    var avPlayer: AVPlayer? { get }
    var playerLayer: AVPlayerLayer? { get }
    #if os(iOS)
    /// Picture in Picture for a transport without an AVPlayerLayer (the engine's software route).
    func pictureInPictureContentSource() -> AVPictureInPictureController.ContentSource?
    func setPictureInPictureActive(_ active: Bool)
    #endif
}

extension PlayerTransport {
    var isPlaying: Bool { phase == .playing || phase == .buffering }

    /// Only the engine shapes the sound: Apple's player over a server stream or a download plays it as it comes.
    var soundReach: SoundReach { .unsupported }

    /// The subtitles showing are teletext, whose caption page can be chosen. Only the engine decodes teletext.
    var showsTeletext: Bool { false }
    /// The teletext caption page; nil, the one the broadcast marks as subtitles.
    var teletextPage: Int? {
        get { nil }
        set {}
    }
}

/// The teletext caption pages broadcasters use most: 888 (the UK, Ireland, the Netherlands), 777 and 150 (Germany
/// and elsewhere), 801 (Sweden).
let teletextPages = [888, 777, 150, 801]

/// A view whose layer is an AVPlayerLayer: the picture of a server stream or a download.
final class PlayerLayerView: UIView {
    override class var layerClass: AnyClass { AVPlayerLayer.self }
    var playerLayer: AVPlayerLayer { layer as! AVPlayerLayer }
}

extension SubtitleSize {
    /// The style rules Apple's player draws WebVTT and other text subtitles with: the profile's size, and its position
    /// as a line along the picture's height (black bars aren't something it can do).
    func textStyleRules(placement: SubtitlePlacement) -> [AVTextStyleRule]? {
        var attributes: [String: Any] = [:]
        if percent != 100 { attributes[kCMTextMarkupAttribute_RelativeFontSize as String] = percent }
        let line: Int? = switch placement.position {
        case .raised: 80
        case .higher: 70
        case .top: 10
        case .bottom, .unknown: nil
        }
        if let line { attributes[kCMTextMarkupAttribute_OrthogonalLinePositionPercentageRelativeToWritingDirection as String] = line }
        return attributes.isEmpty ? nil : AVTextStyleRule(textMarkupAttributes: attributes).map { [$0] }
    }
}

/// Whether the sound settings reach what's playing, for the audio menu.
enum SoundReach {
    /// They're applied, or there's nothing to apply.
    case applied
    /// They're set but can't be applied while AirPlay plays the sound; they come back after it.
    case notOverAirPlay
    /// This way of playing doesn't shape the sound at all (Play files directly is off, or a download).
    case unsupported
}
