import CoreImage
import FlexarrKit
import SwiftUI

/// What Now Playing shows of the episode that's playing, beyond what the player knows: its whole notes, its chapters
/// and its show.
@MainActor
@Observable
final class PlayingEpisode {
    private(set) var episode: PodcastEpisode?
    private(set) var chapters: [PodcastChapter] = []
    private(set) var podcast: Podcast?

    func load(_ id: String?, client: FlexarrClient?) async {
        guard let id, let client else {
            episode = nil
            chapters = []
            podcast = nil
            return
        }
        if episode?.id != id {
            episode = nil
            chapters = []
        }
        async let notes = try? client.podcastEpisode(id)
        async let found = try? client.podcastChapters(id)
        episode = await notes
        chapters = await found ?? []
        if let podcastId = episode?.podcastId, podcast?.id != podcastId {
            podcast = try? await client.podcast(podcastId)
        }
    }
}

/// A slim scrubber: a thin bar that thickens while it's dragged, with a gap where each chapter starts.
struct PlayerScrubber: View {
    @Environment(\.isEnabled) private var isEnabled
    let position: Double
    let duration: Double
    var chapterStarts: [Double] = []
    /// How far VoiceOver's swipes move it, both ways; nil: a podcast's forward 30 and back 15.
    var step: Double? = nil
    /// Where the thumb is while it's dragged; nil when the track's own position shows.
    @Binding var scrubbing: Double?
    let seek: (Double) -> Void

    var body: some View {
        GeometryReader { proxy in
            let width = proxy.size.width
            let shown = scrubbing ?? position
            let fraction = duration > 0 ? min(1, max(0, shown / duration)) : 0
            ZStack(alignment: .leading) {
                Capsule().fill(Color.screen.opacity(0.18))
                Rectangle().fill(Color.screen).frame(width: width * fraction)
                ForEach(chapterStarts.filter { $0 > 0 && $0 < duration }, id: \.self) { start in
                    Rectangle().fill(Color.room).frame(width: 2).offset(x: width * start / duration - 1)
                }
            }
            .frame(height: scrubbing == nil ? 5 : 10)
            .clipShape(Capsule())
            .opacity(isEnabled ? 1 : 0.4)
            .frame(maxHeight: .infinity)
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { drag in
                    guard duration > 0 else { return }
                    scrubbing = min(1, max(0, drag.location.x / width)) * duration
                }
                .onEnded { _ in
                    if let target = scrubbing { seek(target) }
                    scrubbing = nil
                })
            .animation(.spring(response: 0.3, dampingFraction: 0.8), value: scrubbing == nil)
        }
        .frame(height: 28)
        .accessibilityElement()
        .accessibilityLabel("Position")
        .accessibilityValue("\(Format.timestamp(position)) of \(Format.timestamp(duration))")
        .accessibilityAdjustableAction { direction in
            guard isEnabled else { return }
            switch direction {
            case .increment: seek(min(duration, position + (step ?? 30)))
            case .decrement: seek(max(0, position - (step ?? 15)))
            @unknown default: break
            }
        }
    }
}

/// The scheme of the links that jump an episode to a time its notes mention.
private let seekScheme = "flexarr-seek"

/// An episode's notes, with times ("12:34", "1:02:55") that jump there and web addresses that open.
func episodeNotes(_ notes: String, duration: Double?) -> AttributedString {
    var text = AttributedString(notes)
    let ns = notes as NSString
    let whole = NSRange(location: 0, length: ns.length)
    for match in timePattern.matches(in: notes, range: whole) {
        guard let seconds = seconds(ns.substring(with: match.range)), duration.map({ seconds < $0 }) ?? true,
              let range = Range(match.range, in: text) else { continue }
        text[range].link = URL(string: "\(seekScheme)://\(Int(seconds))")
        text[range].font = .subheadline.weight(.semibold).monospacedDigit()
    }
    if let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue) {
        for result in detector.matches(in: notes, range: whole) {
            guard let url = result.url, url.scheme == "http" || url.scheme == "https",
                  let range = Range(result.range, in: text) else { continue }
            text[range].link = url
        }
    }
    return text
}

/// A time on its own ("12:34", "1:02:55"), not part of a longer run of digits and colons.
private let timePattern = try! NSRegularExpression(pattern: #"(?<![\d:])(?:\d{1,2}:)?\d{1,2}:[0-5]\d(?![\d:])"#)

/// "52:50" or "1:02:55" in seconds.
private func seconds(_ time: String) -> Double? {
    let parts = time.split(separator: ":").compactMap { Int($0) }
    guard parts.count >= 2 else { return nil }
    return Double(parts.reduce(0) { $0 * 60 + $1 })
}

extension View {
    /// Times in an episode's notes jump there; other links open as usual.
    func seeksFromNotes(_ seek: @escaping (Double) -> Void) -> some View {
        environment(\.openURL, OpenURLAction { url in
            guard url.scheme == seekScheme, let host = url.host(), let seconds = Double(host) else { return .systemAction }
            seek(seconds)
            return .handled
        })
    }
}

extension UIImage {
    /// The image's average colour, for a glow when the server hasn't given one.
    var averageColor: RGB? {
        guard let input = CIImage(image: self),
              let filter = CIFilter(name: "CIAreaAverage", parameters: [kCIInputImageKey: input, kCIInputExtentKey: CIVector(cgRect: input.extent)]),
              let output = filter.outputImage else { return nil }
        var pixel = [UInt8](repeating: 0, count: 4)
        CIContext(options: [.workingColorSpace: NSNull()])
            .render(output, toBitmap: &pixel, rowBytes: 4, bounds: CGRect(x: 0, y: 0, width: 1, height: 1), format: .RGBA8, colorSpace: nil)
        return RGB(red: Double(pixel[0]) / 255, green: Double(pixel[1]) / 255, blue: Double(pixel[2]) / 255)
    }
}
