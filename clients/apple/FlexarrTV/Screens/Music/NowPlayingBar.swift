import FlexarrKit
import SwiftUI

/// The music that's playing, along the bottom of every screen while it plays: the cover, the track, the artist and
/// where it's got to. It takes no focus (a bar under a long list is a hard thing for the remote to reach), so it
/// can't be closed: it goes once the music is paused or has finished, and comes back with play/pause. The Now Playing
/// tab has the rest, Stop included.
struct NowPlayingBar: View {
    @Environment(AppModel.self) private var model

    /// Whether the bar shows: at once when music starts, and gone a few seconds after it stops, so the moment between
    /// two tracks doesn't make it blink.
    @State private var shown = false

    private var music: MusicPlayer { model.music }

    private var active: Bool { music.current != nil && (music.isPlaying || music.isLoading) }

    var body: some View {
        // A stack even when empty, so the task below is there to bring the bar back.
        VStack(spacing: 0) { bar }
            .animation(.easeInOut(duration: 0.3), value: shown)
            .task(id: active) {
                if !active { try? await Task.sleep(for: .seconds(3)) }
                if !Task.isCancelled { shown = active }
            }
    }

    @ViewBuilder private var bar: some View {
        if let track = music.current, shown {
            HStack(spacing: 24) {
                ZStack {
                    if let artwork = music.artwork {
                        Image(uiImage: artwork).resizable().aspectRatio(contentMode: .fill)
                    } else {
                        SquareArtwork(item: track, size: 64)
                    }
                }
                .frame(width: 64, height: 64)
                .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                VStack(alignment: .leading, spacing: 2) {
                    Text(track.title)
                        .font(.system(size: 25, weight: .semibold))
                        .foregroundStyle(Color.screen)
                        .lineLimit(1)
                    if let artist = model.music.isBook ? model.music.chapter?.title ?? track.bookAuthors : track.artistName {
                        Text(artist)
                            .font(.system(size: 21))
                            .foregroundStyle(Color.usher)
                            .lineLimit(1)
                    }
                }
                Spacer(minLength: 20)
                if music.isLoading {
                    ProgressView().scaleEffect(0.9)
                } else {
                    Image(systemName: "speaker.wave.2.fill")
                        .font(.system(size: 24, weight: .semibold))
                        .foregroundStyle(Color.screen)
                }
                Text("\(Format.timestamp(music.position))  /  \(Format.timestamp(music.duration))")
                    .font(.system(size: 21, weight: .medium))
                    .monospacedDigit()
                    .foregroundStyle(Color.usher)
            }
            .padding(.horizontal, 24)
            .padding(.vertical, 12)
            .frame(maxWidth: .infinity)
            .background {
                RoundedRectangle(cornerRadius: 16, style: .continuous)
                    .fill(Color.seat.opacity(0.96))
                    .shadow(color: .black.opacity(0.45), radius: 24, y: 8)
            }
            .overlay {
                RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Color.aisle, lineWidth: 2)
            }
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 20)
            .transition(.move(edge: .bottom).combined(with: .opacity))
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Now playing: \(track.title)\(track.artistName.map { " by \($0)" } ?? "")")
        }
    }
}
