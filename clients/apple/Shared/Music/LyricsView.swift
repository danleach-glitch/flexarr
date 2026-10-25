import FlexarrKit
import Observation
import SwiftUI

/// Fetches the lyrics of the track that's playing, once per track, so Now Playing knows whether it has any to offer
/// before the viewer asks. A track without lyrics (or a server before them) leaves `lyrics` nil.
@MainActor
@Observable
final class LyricsLoader {
    private(set) var lyrics: Lyrics?
    private(set) var trackId: String?

    /// Loads `track`'s lyrics, unless they're already here. Run from a task keyed by the track's id, so a change of
    /// track cancels the request for the last one.
    func load(_ track: ItemSummary?, client: FlexarrClient?) async {
        guard let track, let client else {
            lyrics = nil
            trackId = nil
            return
        }
        guard trackId != track.id else { return }
        trackId = track.id
        lyrics = nil
        let found = try? await client.lyrics(of: track.id)
        guard trackId == track.id else { return } // Skipped past while they were fetched.
        lyrics = found
    }
}

/// A track's lyrics, line by line. Synced ones follow the music: the line being sung is lit and kept in the middle,
/// and, where `onSeek` is given, pressing a line jumps the track to it. Plain ones just read. Following the song moves
/// the list without animation when the viewer asks for reduced motion.
struct LyricsView: View {
    let lyrics: Lyrics
    /// Where playback is, in seconds.
    let position: Double
    var onSeek: ((Double) -> Void)?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    #if os(tvOS)
    @FocusState private var focusedLine: Int?
    #endif

    private var active: Int? { lyrics.activeLine(at: position) }

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: Metrics.spacing) {
                    ForEach(Array(lyrics.lines.enumerated()), id: \.offset) { index, line in
                        LyricLineView(text: line.text, emphasis: emphasis(of: index), seek: seek(to: line))
                            .id(index)
                            #if os(tvOS)
                            .focused($focusedLine, equals: index)
                            #endif
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, Metrics.inset) // Room for the first and last lines to sit in the middle too.
            }
            .scrollIndicators(.hidden)
            .onAppear { follow(active, with: proxy, animated: false) }
            .onChange(of: active) { _, line in follow(line, with: proxy, animated: true) }
        }
        .accessibilityLabel("Lyrics")
    }

    private func emphasis(of index: Int) -> LyricLineView.Emphasis {
        guard lyrics.synced else { return .plain }
        return index == active ? .active : .dimmed
    }

    private func seek(to line: LyricLine) -> (() -> Void)? {
        if lyrics.synced, let onSeek, let time = line.time { return { onSeek(time) } }
        #if os(tvOS)
        return {} // Every line takes focus on the TV, so the list can be read through with the remote (and Menu goes back).
        #else
        return nil
        #endif
    }

    private func follow(_ line: Int?, with proxy: ScrollViewProxy, animated: Bool) {
        guard let line else { return }
        if animated, !reduceMotion {
            withAnimation(.easeInOut(duration: 0.45)) { proxy.scrollTo(line, anchor: .center) }
        } else {
            proxy.scrollTo(line, anchor: .center)
        }
        #if os(tvOS)
        focusedLine = line // Focus rides along, so a press from here seeks to the line that's up.
        #endif
    }

    private enum Metrics {
        #if os(tvOS)
        static let spacing: CGFloat = 22
        static let inset: CGFloat = 300
        #else
        static let spacing: CGFloat = 10
        static let inset: CGFloat = 120
        #endif
    }
}

/// One line: lit while it's sung, dimmed when it isn't, or plain in lyrics that aren't synced. A button when it can seek.
private struct LyricLineView: View {
    enum Emphasis { case active, dimmed, plain }

    let text: String
    let emphasis: Emphasis
    var seek: (() -> Void)?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Group {
            if let seek {
                Button(action: seek) { label }
                    .buttonStyle(LyricLineStyle())
            } else {
                label.padding(.horizontal, LyricLineStyle.horizontalPadding).padding(.vertical, LyricLineStyle.verticalPadding)
            }
        }
        .foregroundStyle(colour)
        .animation(reduceMotion ? nil : .easeInOut(duration: 0.25), value: emphasis)
    }

    private var label: some View {
        Text(text.isEmpty ? " " : text) // An empty line between verses keeps its place.
            .font(LyricLineStyle.font)
            .multilineTextAlignment(.leading)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var colour: Color {
        switch emphasis {
        case .active: Color.screen
        case .dimmed: Color.screen.opacity(0.4)
        case .plain: Color.screen.opacity(0.9)
        }
    }
}

/// A line that can be pressed: it keeps its own colour and, on the TV, lifts a touch on focus.
private struct LyricLineStyle: ButtonStyle {
    #if os(tvOS)
    static let font = Font.system(size: 44, weight: .bold)
    static let horizontalPadding: CGFloat = 28
    static let verticalPadding: CGFloat = 8
    #else
    static let font = Font.system(size: 24, weight: .bold)
    static let horizontalPadding: CGFloat = 12
    static let verticalPadding: CGFloat = 4
    #endif

    func makeBody(configuration: Configuration) -> some View {
        LineBody(configuration: configuration)
    }

    private struct LineBody: View {
        let configuration: Configuration
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .padding(.horizontal, LyricLineStyle.horizontalPadding)
                .padding(.vertical, LyricLineStyle.verticalPadding)
                .background {
                    if isFocused {
                        RoundedRectangle(cornerRadius: 16, style: .continuous).fill(Color.screen.opacity(0.12))
                    }
                }
                .opacity(configuration.isPressed ? 0.6 : 1)
                .scaleEffect(isFocused ? 1.02 : 1)
                .animation(.spring(response: 0.3, dampingFraction: 0.75), value: isFocused)
        }
    }
}
