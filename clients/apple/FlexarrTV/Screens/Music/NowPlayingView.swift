import FlexarrKit
import SwiftUI

/// The music that's playing, as its own tab while there is any: the cover, the track's name, how far it's got, the
/// transport controls and what comes next. The remote's play/pause works here as everywhere; Menu goes back to the
/// screen Play or Shuffle was pressed on (see `MainTabView`), or else to the sidebar, with the music on. A Lyrics
/// control, there when the track has any, opens the words as a page of their own, which Menu closes.
struct NowPlayingView: View {
    @Environment(AppModel.self) private var model
    @FocusState private var focusedControl: Control?
    @State private var lyrics = LyricsLoader()
    /// Lyrics are wanted; the page shows while the track that's playing has some.
    @State private var showingLyrics = false
    /// The cover's colours for the visualiser, read once per cover.
    @State private var palette: [CoverPalette.RGB] = []
    @State private var visualiser = AudioSpectrum.isOn
    @State private var onScreen = false

    private enum Control: Hashable { case shuffle, previous, playPause, next, `repeat`, lyrics }

    private var music: MusicPlayer { model.music }

    var body: some View {
        ZStack {
            if visualiser, music.current != nil {
                MusicAurora(colours: palette, spectrum: music.spectrum, playing: music.isPlaying)
                    .transition(.opacity)
            } else {
                RoomBackground(ambient: music.current?.glowColor, intensity: 0.5)
            }
            if let track = music.current {
                if showingLyrics, let found = lyrics.lyrics {
                    lyricsPage(track, found)
                } else {
                    HStack(alignment: .center, spacing: 100) {
                        cover(track, size: 640)
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
                }
            } else {
                StatusView(state: .empty("Nothing is playing."))
            }
        }
        .defaultFocus($focusedControl, .playPause)
        .task(id: music.current?.id) { await lyrics.load(music.current, client: model.client) }
        .task(id: PaletteSource(track: music.current?.id, hasArtwork: music.artwork != nil)) { readPalette() }
        .onAppear {
            visualiser = AudioSpectrum.isOn
            onScreen = true
        }
        .onDisappear { onScreen = false }
        // The visualiser is there to be watched: no screensaver over it while the music plays.
        .onChange(of: onScreen && visualiser && music.isPlaying) { _, awake in
            UIApplication.shared.isIdleTimerDisabled = awake
        }
    }

    /// The words beside a smaller cover, the track's name and the timeline. Menu comes back here.
    private func lyricsPage(_ track: ItemSummary, _ found: Lyrics) -> some View {
        HStack(alignment: .top, spacing: 90) {
            VStack(alignment: .leading, spacing: 36) {
                cover(track, size: 400)
                titles(track, compact: true)
                timeline
            }
            .frame(width: 520)
            .padding(.top, 20)
            LyricsView(lyrics: found, position: music.position) { music.seek(to: $0) }
                .frame(maxWidth: .infinity)
        }
        .padding(.horizontal, Layout.edge + 40)
        .padding(.top, 110)
        .padding(.bottom, 40)
        .onExitCommand {
            showingLyrics = false
            focusedControl = .lyrics
        }
    }

    /// What the palette is read from: the track, and whether its cover has arrived yet.
    private struct PaletteSource: Equatable {
        var track: String?
        var hasArtwork: Bool
    }

    /// The cover's colours, or (until it arrives, or for a grey cover) the track's own glow and its neighbours.
    private func readPalette() {
        guard visualiser, let track = music.current else { return }
        let glow = track.ambient.glow
        let fallback = CoverPalette.RGB(r: glow.red * 255, g: glow.green * 255, b: glow.blue * 255)
        withAnimation(.easeInOut(duration: 1.2)) { palette = CoverPalette.colours(of: music.artwork, fallback: fallback) }
    }

    private func cover(_ track: ItemSummary, size: CGFloat) -> some View {
        ZStack {
            if let artwork = music.artwork {
                Image(uiImage: artwork)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
            } else {
                SquareArtwork(item: track, size: size)
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
        .shadow(color: .black.opacity(0.5), radius: 40, y: 24)
        .shadow(color: track.glowColor.opacity(0.35), radius: 80)
        .animation(.easeOut(duration: 0.35), value: track.id)
    }

    private func titles(_ track: ItemSummary, compact: Bool = false) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(track.title)
                .font(.marquee(compact ? 44 : 64, weight: .heavy))
                .foregroundStyle(Color.screen)
                .lineLimit(2)
                .minimumScaleFactor(0.6)
            Text([track.artistName, music.albumTitle(of: track)].compactMap { $0 }.joined(separator: "   "))
                .font(.system(size: compact ? 26 : 30, weight: .medium))
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
            if lyrics.lyrics != nil {
                Button { showingLyrics = true } label: { Label("Lyrics", systemImage: "quote.bubble") }
                    .buttonStyle(RoundControlStyle(size: 84))
                    .focused($focusedControl, equals: .lyrics)
            }
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
