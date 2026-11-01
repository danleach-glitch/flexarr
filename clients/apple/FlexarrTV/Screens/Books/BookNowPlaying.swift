import FlexarrKit
import SwiftUI

/// A book playing, on the big screen over the music visualiser's aurora: the cover, the book and author, the chapter
/// playing, a timeline for the chapter or the whole book (a press on it switches), the controls (chapters either
/// side, the listener's skips, speed) and the chapter list, which scrolls in what's left of the screen.
struct BookNowPlaying<Cover: View>: View {
    @Environment(AppModel.self) private var model
    let track: ItemSummary
    @ViewBuilder var cover: (CGFloat) -> Cover
    @FocusState private var focused: Control?
    /// The timeline shows the chapter (the default, as in book apps) or the whole book.
    @AppStorage("books.timelineIsBook") private var wholeBook = false

    private enum Control: Hashable { case previousChapter, back, playPause, forward, nextChapter, speed, timeline, offer, stop }

    private var music: MusicPlayer { model.music }

    var body: some View {
        HStack(alignment: .center, spacing: 100) {
            cover(560)
            VStack(alignment: .leading, spacing: 34) {
                titles
                offers
                timeline
                controls
                chapterList
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .padding(.horizontal, Layout.edge + 40)
        .padding(.top, 110)
        .padding(.bottom, 40)
        .defaultFocus($focused, .playPause)
        // Now Playing's own default focus (music's play button) isn't this one's: Play/Pause is put in focus here.
        .task {
            try? await Task.sleep(for: .milliseconds(300))
            if focused == nil || focused == .timeline { focused = .playPause }
        }
    }

    private var titles: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(track.title)
                .font(.marquee(58, weight: .heavy))
                .foregroundStyle(Color.screen)
                .lineLimit(2)
                .minimumScaleFactor(0.6)
            if let authors = track.bookAuthors {
                Text(authors)
                    .font(.system(size: 30, weight: .medium))
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
            }
            if let chapter = music.chapter, music.chapters.count > 1 {
                Text(chapter.title)
                    .font(.system(size: 30, weight: .semibold))
                    .foregroundStyle(Color.screen.opacity(0.9))
                    .lineLimit(1)
                    .contentTransition(.opacity)
                    .animation(.easeOut(duration: 0.3), value: chapter.index)
            }
        }
    }

    /// Jump to where another device got to, or the series' next book once this one has ended.
    @ViewBuilder
    private var offers: some View {
        if let offer = music.jumpOffer {
            HStack(spacing: 24) {
                Button { music.acceptJump() } label: {
                    Label("Jump to \(Format.timestamp(offer.positionSeconds))\(offer.device.map { " from \($0)" } ?? "")", systemImage: "arrow.uturn.forward")
                }
                .buttonStyle(ActionButtonStyle(ambient: track.glowColor, prominent: true))
                .focused($focused, equals: .offer)
                Button { music.dismissJump() } label: { Label("Not now", systemImage: "xmark") }
                    .buttonStyle(ActionButtonStyle(iconOnly: true))
            }
            .focusSection()
        } else if let next = music.nextBookOffer {
            HStack(spacing: 24) {
                Button { music.playNextBook() } label: { Label("Play \(next.title)", systemImage: "play.fill") }
                    .buttonStyle(ActionButtonStyle(ambient: next.glowColor, prominent: true))
                    .focused($focused, equals: .offer)
            }
            .focusSection()
        }
    }

    /// The chapter's (or the whole book's) progress: a press switches which.
    private var timeline: some View {
        let chapter = music.chapters.count > 1 ? music.chapter : nil
        let showsChapter = !wholeBook && chapter != nil
        let start = showsChapter ? chapter?.startSeconds ?? 0 : 0
        let length = showsChapter ? chapter?.durationSeconds ?? 0 : music.duration
        let into = max(0, music.position - start)
        return Button { wholeBook.toggle() } label: {
            VStack(spacing: 14) {
                ProgressBar(value: length > 0 ? min(1, into / length) : 0)
                HStack {
                    Text(Format.timestamp(into))
                    Spacer()
                    Text(showsChapter ? "Chapter" : "Book")
                        .foregroundStyle(Color.screen.opacity(0.7))
                    Spacer()
                    Text(length > 0 ? "-" + Format.timestamp(max(0, length - into) / max(0.5, music.speed)) : "")
                }
                .font(.system(size: 24, weight: .medium))
                .monospacedDigit()
                .foregroundStyle(Color.usher)
            }
            .padding(.vertical, 10)
            .padding(.horizontal, 16)
        }
        .buttonStyle(TimelineStyle())
        .focused($focused, equals: .timeline)
        .frame(maxWidth: 900)
        .accessibilityLabel(showsChapter ? "Chapter progress" : "Book progress")
        .accessibilityHint("Switches between the chapter and the whole book")
    }

    private var controls: some View {
        let back = Int(music.skipBack), forward = Int(music.skipForward)
        return HStack(spacing: 30) {
            Button { music.previousChapter() } label: { Label("Previous chapter", systemImage: "backward.end.fill") }
                .buttonStyle(RoundControlStyle(size: 84))
                .focused($focused, equals: .previousChapter)
            Button { music.skip(by: -music.skipBack) } label: { Label("Back \(back) seconds", systemImage: "gobackward.\(back)") }
                .buttonStyle(RoundControlStyle(size: 96))
                .focused($focused, equals: .back)
            Button { music.togglePlayPause() } label: {
                Label(music.isPlaying ? "Pause" : "Play", systemImage: music.isPlaying ? "pause.fill" : "play.fill")
            }
            .buttonStyle(RoundControlStyle(size: 120, active: true))
            .focused($focused, equals: .playPause)
            Button { music.skip(by: music.skipForward) } label: { Label("Forward \(forward) seconds", systemImage: "goforward.\(forward)") }
                .buttonStyle(RoundControlStyle(size: 96))
                .focused($focused, equals: .forward)
            Button { music.nextChapter() } label: { Label("Next chapter", systemImage: "forward.end.fill") }
                .buttonStyle(RoundControlStyle(size: 84))
                .focused($focused, equals: .nextChapter)
                .disabled((music.chapterIndex ?? 0) + 1 >= music.chapters.count)
            // Speed steps through the usual ones; the phone and the web pick any from 0.5× to 3.5×.
            Button {
                let speeds = MusicPlayer.bookLockScreenSpeeds
                music.setSpeed(speeds.first { $0 > music.speed + 0.01 } ?? speeds[0])
            } label: {
                Text(BookSpeed.label(music.speed))
                    .font(.system(size: 26, weight: .bold))
                    .monospacedDigit()
            }
            .buttonStyle(RoundControlStyle(size: 84, active: music.speed != 1))
            .focused($focused, equals: .speed)
            .accessibilityLabel("Speed, \(BookSpeed.label(music.speed))")
            Button { music.stop() } label: { Label("Stop", systemImage: "stop.fill") }
                .buttonStyle(RoundControlStyle(size: 84))
                .focused($focused, equals: .stop)
            if music.isLoading { ProgressView().scaleEffect(1.2) }
        }
        .focusSection()
    }

    /// Every chapter, the one playing marked; pressing one goes there.
    @ViewBuilder
    private var chapterList: some View {
        let chapters = music.chapters
        if chapters.count > 1 {
            let current = music.chapterIndex
            ScrollViewReader { proxy in
                ScrollView(.vertical) {
                    LazyVStack(alignment: .leading, spacing: 8) {
                        ForEach(chapters) { chapter in
                            ChapterRow(chapter: chapter, isCurrent: chapter.index == chapters[safeIndex: current]?.index,
                                       heard: (current ?? -1) > (chapters.firstIndex(of: chapter) ?? 0), isPlaying: music.isPlaying) {
                                music.seek(to: chapter.startSeconds)
                            }
                            .id(chapter.index)
                        }
                    }
                    .padding(.horizontal, 20) // room for the focused row's lift
                    .padding(.bottom, 60)
                }
                .padding(.horizontal, -20)
                .onAppear { if let current { proxy.scrollTo(chapters[current].index, anchor: .top) } }
            }
            .frame(maxWidth: 900, maxHeight: .infinity, alignment: .topLeading)
            .focusSection()
        }
    }
}

private extension Array {
    subscript(safeIndex index: Int?) -> Element? {
        guard let index, indices.contains(index) else { return nil }
        return self[index]
    }
}

/// The timeline as a focusable control: a soft seat that lights on focus.
private struct TimelineStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        SeatBody(configuration: configuration)
    }

    private struct SeatBody: View {
        let configuration: Configuration
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .background {
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .fill(Color.screen.opacity(isFocused ? 0.16 : 0))
                }
                .scaleEffect(isFocused ? 1.02 : 1)
                .animation(.easeOut(duration: 0.2), value: isFocused)
        }
    }
}
