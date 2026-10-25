import FlexarrKit
import SwiftUI

/// The music that's playing, as its own tab while there is any: the cover, the track's name, how far it's got, the
/// transport controls and what comes next. The remote's play/pause works here as everywhere; Menu goes back to the
/// tab bar with the music on.
struct NowPlayingView: View {
    @Environment(AppModel.self) private var model
    @FocusState private var focusedControl: Control?

    private enum Control: Hashable { case shuffle, previous, playPause, next, `repeat` }

    private var music: MusicPlayer { model.music }

    var body: some View {
        ZStack {
            RoomBackground(ambient: music.current?.glowColor, intensity: 0.5)
            if let track = music.current {
                HStack(alignment: .center, spacing: 100) {
                    cover(track)
                    VStack(alignment: .leading, spacing: 40) {
                        titles(track)
                        timeline
                        controls
                        upNext
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                }
                .padding(.horizontal, Layout.edge + 40)
                .padding(.top, 130)
                .padding(.bottom, 60)
            } else {
                StatusView(state: .empty("Nothing is playing."))
            }
        }
        .defaultFocus($focusedControl, .playPause)
    }

    private func cover(_ track: ItemSummary) -> some View {
        ZStack {
            if let artwork = music.artwork {
                Image(uiImage: artwork)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
            } else {
                SquareArtwork(item: track, size: 640)
            }
        }
        .frame(width: 640, height: 640)
        .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
        .shadow(color: .black.opacity(0.5), radius: 40, y: 24)
        .shadow(color: track.glowColor.opacity(0.35), radius: 80)
        .animation(.easeOut(duration: 0.35), value: track.id)
    }

    private func titles(_ track: ItemSummary) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(track.title)
                .font(.marquee(64, weight: .heavy))
                .foregroundStyle(Color.screen)
                .lineLimit(2)
                .minimumScaleFactor(0.6)
            Text([track.artistName, music.albumTitle(of: track)].compactMap { $0 }.joined(separator: "   "))
                .font(.system(size: 30, weight: .medium))
                .foregroundStyle(Color.usher)
                .lineLimit(1)
        }
    }

    private var timeline: some View {
        VStack(spacing: 14) {
            ProgressBar(value: music.duration > 0 ? music.position / music.duration : 0)
            HStack {
                Text(Format.timestamp(music.position))
                Spacer()
                Text(music.duration > 0 ? "-" + Format.timestamp(max(0, music.duration - music.position)) : "")
            }
            .font(.system(size: 24, weight: .medium))
            .monospacedDigit()
            .foregroundStyle(Color.usher)
        }
        .frame(maxWidth: 900)
    }

    private var controls: some View {
        HStack(spacing: 36) {
            Button { music.toggleShuffle() } label: { Label("Shuffle", systemImage: "shuffle") }
                .buttonStyle(RoundControlStyle(size: 84, active: music.isShuffled))
                .focused($focusedControl, equals: .shuffle)
            Button { music.previous() } label: { Label("Previous", systemImage: "backward.fill") }
                .buttonStyle(RoundControlStyle(size: 96))
                .focused($focusedControl, equals: .previous)
            Button { music.togglePlayPause() } label: {
                Label(music.isPlaying ? "Pause" : "Play", systemImage: music.isPlaying ? "pause.fill" : "play.fill")
            }
            .buttonStyle(RoundControlStyle(size: 120, active: true))
            .focused($focusedControl, equals: .playPause)
            Button { music.next() } label: { Label("Next", systemImage: "forward.fill") }
                .buttonStyle(RoundControlStyle(size: 96))
                .focused($focusedControl, equals: .next)
                .disabled(!music.hasNext)
            Button { music.cycleRepeat() } label: {
                Label("Repeat", systemImage: music.repeatMode == .one ? "repeat.1" : "repeat")
            }
            .buttonStyle(RoundControlStyle(size: 84, active: music.repeatMode != .off))
            .focused($focusedControl, equals: .repeat)
            if music.isLoading {
                ProgressView().scaleEffect(1.2)
            }
        }
        .focusSection()
    }

    /// The whole queue, in a list that scrolls within what's left of the screen: an album or a playlist runs well
    /// past the bottom, and moving down brings the next row into view.
    @ViewBuilder
    private var upNext: some View {
        let next = music.upNext
        if !next.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                Text("Up next")
                    .font(.marquee(32, weight: .bold))
                    .foregroundStyle(Color.screen)
                    .padding(.leading, 30)
                    .padding(.bottom, 6)
                ScrollView(.vertical) {
                    LazyVStack(alignment: .leading, spacing: 8) {
                        ForEach(Array(next.enumerated()), id: \.offset) { offset, track in
                            TrackRow(track: track, number: offset + 1, isCurrent: false, isPlaying: false) {
                                music.skip(to: music.index + 1 + offset)
                            }
                        }
                    }
                    .padding(.horizontal, 20) // room for the focused row's lift, which would otherwise be clipped
                    .padding(.bottom, 60)
                }
                .padding(.horizontal, -20)
            }
            .frame(maxWidth: 900, maxHeight: .infinity, alignment: .topLeading)
            .focusSection()
        }
    }
}
