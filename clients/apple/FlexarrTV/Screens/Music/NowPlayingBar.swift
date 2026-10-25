import FlexarrKit
import SwiftUI

/// The music that's playing, along the bottom of every screen while there is any: the cover, the track, the artist
/// and where it's got to. It takes no focus (a bar under a long list is a hard thing for the remote to reach): the
/// remote's play/pause works everywhere, and the Now Playing tab has the rest.
struct NowPlayingBar: View {
    @Environment(AppModel.self) private var model

    private var music: MusicPlayer { model.music }

    var body: some View {
        if let track = music.current {
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
                    if let artist = track.artistName {
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
                    Image(systemName: music.isPlaying ? "speaker.wave.2.fill" : "pause.fill")
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
