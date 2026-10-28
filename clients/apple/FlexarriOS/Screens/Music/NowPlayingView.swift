import FlexarrKit
import SwiftUI

/// The music that's playing, as a sheet: the cover, the track's name, a scrubber, the transport controls, AirPlay,
/// and what comes next. A Lyrics button, there when the track has any, swaps the cover and queue for the words,
/// which follow the song. A podcast episode gets a podcast's controls instead: back 15 seconds and forward 30, its
/// speed and a sleep timer.
struct NowPlayingView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @Environment(\.horizontalSizeClass) private var sizeClass
    /// Where the thumb is while the viewer drags it; nil when the track's own position shows.
    @State private var scrubbing: Double?
    @State private var lyrics = LyricsLoader()
    /// Lyrics are wanted; they show while the track that's playing has some.
    @State private var showingLyrics = false

    private var music: MusicPlayer { model.music }

    var body: some View {
        ZStack {
            RoomBackground(ambient: music.current?.glowColor, intensity: 0.45)
            if let track = music.current {
                if showingLyrics, let found = lyrics.lyrics {
                    lyricsPanel(track, found)
                } else {
                    ScrollView(.vertical) {
                        VStack(spacing: 24) {
                            grabber
                            cover(track)
                                .padding(.top, 8)
                            titles(track)
                            scrubber
                            controls
                            extras
                            upNext
                        }
                        .padding(.horizontal, 28)
                        .padding(.bottom, 32)
                        .frame(maxWidth: 560)
                        .frame(maxWidth: .infinity)
                    }
                }
            } else {
                StatusView(state: .empty("Nothing is playing."))
            }
        }
        .task(id: music.current?.id) { await lyrics.load(music.isPodcast ? nil : music.current, client: model.client) }
        .task(id: music.current?.id) { if music.isPodcast { await model.loadUpNext() } }
        .onChange(of: music.current == nil) { _, empty in if empty { dismiss() } }
    }

    private var grabber: some View {
        Capsule().fill(Color.aisle).frame(width: 40, height: 5).padding(.top, 10)
    }

    /// The words, with the track's name and cover kept small above them and the controls kept below.
    private func lyricsPanel(_ track: ItemSummary, _ found: Lyrics) -> some View {
        VStack(spacing: 20) {
            grabber
            HStack(spacing: 14) {
                artwork(track)
                    .frame(width: 56, height: 56)
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                VStack(alignment: .leading, spacing: 2) {
                    Text(track.title)
                        .font(.headline)
                        .foregroundStyle(Color.screen)
                    if let artist = track.artistName {
                        Text(artist)
                            .font(.subheadline.weight(.medium))
                            .foregroundStyle(Color.usher)
                    }
                }
                .lineLimit(1)
                Spacer(minLength: 0)
            }
            .padding(.top, 8)
            LyricsView(lyrics: found, position: music.position) { music.seek(to: $0) }
                .frame(maxHeight: .infinity)
            scrubber
            controls
            extras
        }
        .padding(.horizontal, 28)
        .padding(.bottom, 32)
        .frame(maxWidth: 560)
        .frame(maxWidth: .infinity)
    }

    private func artwork(_ track: ItemSummary) -> some View {
        ZStack {
            if let artwork = music.artwork {
                Image(uiImage: artwork).resizable().aspectRatio(contentMode: .fill)
            } else {
                SquareArtwork(item: track, imageWidth: 800)
            }
        }
    }

    private func cover(_ track: ItemSummary) -> some View {
        artwork(track)
            .aspectRatio(1, contentMode: .fit)
            .frame(maxWidth: sizeClass == .regular ? 420 : 320)
            .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
            .shadow(color: .black.opacity(0.5), radius: 24, y: 12)
            .shadow(color: track.glowColor.opacity(0.35), radius: 40)
            // Smaller while paused, as every music app does.
            .scaleEffect(music.isPlaying ? 1 : 0.9)
            .animation(.spring(response: 0.45, dampingFraction: 0.8), value: music.isPlaying)
            .animation(.easeOut(duration: 0.3), value: track.id)
    }

    private func titles(_ track: ItemSummary) -> some View {
        VStack(spacing: 4) {
            Text(track.title)
                .font(.title3.weight(.bold))
                .foregroundStyle(Color.screen)
                .lineLimit(2)
                .multilineTextAlignment(.center)
            HStack(spacing: 6) {
                Text([track.artistName, music.albumTitle(of: track)].compactMap { $0 }.joined(separator: "  –  "))
                    .lineLimit(1)
                if music.isPlayingOffline {
                    DownloadedMark()
                }
            }
            .font(.subheadline.weight(.medium))
            .foregroundStyle(Color.usher)
        }
    }

    private var scrubber: some View {
        VStack(spacing: 4) {
            Slider(value: Binding(
                get: { scrubbing ?? music.position },
                set: { scrubbing = $0 }
            ), in: 0...max(1, music.duration)) { editing in
                if !editing, let target = scrubbing {
                    music.seek(to: target)
                    scrubbing = nil
                }
            }
            .tint(Color.screen)
            HStack {
                Text(Format.timestamp(scrubbing ?? music.position))
                Spacer()
                Text(music.duration > 0 ? "-" + Format.timestamp(max(0, music.duration - (scrubbing ?? music.position))) : "")
            }
            .font(.caption.weight(.medium))
            .monospacedDigit()
            .foregroundStyle(Color.usher)
        }
    }

    @ViewBuilder
    private var controls: some View {
        if music.isPodcast { podcastControls } else { musicControls }
    }

    private var podcastControls: some View {
        HStack(spacing: 0) {
            Spacer()
            Button { music.skip(by: -15) } label: { Label("Back 15 seconds", systemImage: "gobackward.15") }
                .buttonStyle(TransportStyle(size: 56))
            Spacer()
            playButton
            Spacer()
            Button { music.skip(by: 30) } label: { Label("Forward 30 seconds", systemImage: "goforward.30") }
                .buttonStyle(TransportStyle(size: 56))
            Spacer()
        }
    }

    private var playButton: some View {
        Button { music.togglePlayPause() } label: {
            if music.isLoading {
                ProgressView().tint(Color.screen)
            } else {
                Label(music.isPlaying ? "Pause" : "Play", systemImage: music.isPlaying ? "pause.fill" : "play.fill")
            }
        }
        .buttonStyle(TransportStyle(size: 76, filled: true))
    }

    private var musicControls: some View {
        HStack(spacing: 0) {
            Button { music.toggleShuffle() } label: { Label("Shuffle", systemImage: "shuffle") }
                .buttonStyle(TransportStyle(size: 44, active: music.isShuffled))
            Spacer()
            Button { music.previous() } label: { Label("Previous", systemImage: "backward.fill") }
                .buttonStyle(TransportStyle(size: 56))
            Spacer()
            playButton
            Spacer()
            Button { music.next() } label: { Label("Next", systemImage: "forward.fill") }
                .buttonStyle(TransportStyle(size: 56))
                .disabled(!music.hasNext)
            Spacer()
            Button { music.cycleRepeat() } label: {
                Label("Repeat", systemImage: music.repeatMode == .one ? "repeat.1" : "repeat")
            }
            .buttonStyle(TransportStyle(size: 44, active: music.repeatMode != .off))
        }
    }

    /// AirPlay, and Lyrics when the track has some; a podcast's speed and sleep timer.
    private var extras: some View {
        HStack(spacing: 36) {
            Spacer()
            if music.isPodcast { speedMenu }
            RoutePicker().frame(width: 44, height: 44)
            if music.isPodcast { sleepMenu }
            if lyrics.lyrics != nil {
                Button { showingLyrics.toggle() } label: { Label("Lyrics", systemImage: "quote.bubble") }
                    .buttonStyle(TransportStyle(size: 44, active: showingLyrics))
            }
            Spacer()
        }
    }

    private var speedMenu: some View {
        Menu {
            Picker("Speed", selection: Binding(get: { music.speed }, set: { music.setSpeed($0) })) {
                ForEach(MusicPlayer.speeds, id: \.self) { Text(speedLabel($0)).tag($0) }
            }
        } label: {
            Text(speedLabel(music.speed))
                .font(.subheadline.weight(.bold))
                .monospacedDigit()
                .foregroundStyle(music.speed == 1 ? Color.screen : Color.silver)
                .frame(minWidth: 44, minHeight: 44)
        }
        .accessibilityLabel("Speed, \(speedLabel(music.speed))")
    }

    private var sleepMenu: some View {
        Menu {
            Picker("Sleep timer", selection: Binding(get: { music.sleep.setting }, set: { music.sleep.set($0) })) {
                Text("Off").tag(SleepTimer.Setting?.none)
                ForEach(SleepTimer.minuteChoices, id: \.self) { Text(SleepTimer.Setting.minutes($0).label).tag(SleepTimer.Setting?.some(.minutes($0))) }
                Text(SleepTimer.Setting.endOfEpisode.label).tag(SleepTimer.Setting?.some(.endOfEpisode))
            }
        } label: {
            HStack(spacing: 4) {
                Image(systemName: music.sleep.setting == nil ? "moon.zzz" : "moon.zzz.fill")
                if let end = music.sleep.endsAt {
                    // Minutes left, counting down.
                    TimelineView(.periodic(from: .now, by: 1)) { context in
                        Text(Format.timestamp(max(0, end.timeIntervalSince(context.date))))
                            .font(.caption.weight(.semibold))
                            .monospacedDigit()
                    }
                }
            }
            .font(.system(size: 20, weight: .semibold))
            .foregroundStyle(music.sleep.setting == nil ? Color.screen : Color.silver)
            .frame(minWidth: 44, minHeight: 44)
        }
        .accessibilityLabel(music.sleep.setting == nil ? "Sleep timer" : "Sleep timer on")
    }

    @ViewBuilder
    private var upNext: some View {
        if music.isPodcast { podcastUpNext } else { musicUpNext }
    }

    /// A podcast's Up Next, as the server keeps it: tapping one plays it.
    @ViewBuilder
    private var podcastUpNext: some View {
        let next = model.upNext.filter { $0.id != music.current?.id }
        if !next.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                Text("Up Next")
                    .font(.marquee(22, weight: .bold))
                    .foregroundStyle(Color.screen)
                    .padding(.horizontal, 12)
                    .padding(.top, 8)
                ForEach(next.prefix(20)) { episode in
                    Button { model.playEpisode(episode.summary) } label: {
                        HStack(spacing: 12) {
                            PodcastArtwork(path: episode.artwork.thumb ?? episode.artwork.poster, title: episode.podcastTitle,
                                           accent: episode.accentColor, imageWidth: 185, cornerRadius: 6)
                                .frame(width: 44, height: 44)
                            VStack(alignment: .leading, spacing: 1) {
                                Text(episode.title)
                                    .font(.subheadline.weight(.semibold))
                                    .foregroundStyle(Color.screen)
                                Text(episode.podcastTitle)
                                    .font(.caption)
                                    .foregroundStyle(Color.usher)
                            }
                            .lineLimit(1)
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, 12)
                        .padding(.vertical, 6)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    @ViewBuilder
    private var musicUpNext: some View {
        let next = Array(music.upNext.prefix(20))
        if !next.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                Text("Up next")
                    .font(.marquee(22, weight: .bold))
                    .foregroundStyle(Color.screen)
                    .padding(.horizontal, 12)
                    .padding(.top, 8)
                ForEach(Array(next.enumerated()), id: \.element.id) { offset, track in
                    TrackRow(track: track, number: offset + 1, isCurrent: false, isPlaying: false) {
                        music.skip(to: music.index + 1 + offset)
                    }
                }
            }
        }
    }
}

/// Transport controls: an icon alone, or on a projected-white disc for Play. `active` lights shuffle and repeat.
private struct TransportStyle: ButtonStyle {
    var size: CGFloat
    var filled = false
    var active = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .labelStyle(.iconOnly)
            .font(.system(size: size * (filled ? 0.42 : 0.5), weight: .semibold))
            .foregroundStyle(filled ? Color.room : (active ? Color.silver : Color.screen))
            .frame(width: size, height: size)
            .background {
                if filled { Circle().fill(Color.screen) }
            }
            .opacity(configuration.isPressed ? 0.7 : 1)
            .scaleEffect(configuration.isPressed ? 0.94 : 1)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }
}
