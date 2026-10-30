import FlexarrKit
import SwiftUI

/// The music (or podcast) that's playing, in a bar above the tab bar while there is any: a close button, cover, track,
/// artist, a play/pause button, next (a podcast's forward 30) and a thin progress line. Tapping it opens Now Playing;
/// the close button stops playback (keeping an episode's place) and takes the bar away.
struct NowPlayingBar: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router

    private var music: MusicPlayer { model.music }

    var body: some View {
        if let track = music.current {
            HStack(spacing: 12) {
                Button {
                    withAnimation { music.stop() }
                } label: {
                    Image(systemName: "xmark")
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.usher)
                        .frame(width: 28, height: 44)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Stop and close")
                .padding(.trailing, -6)
                Button { router.showsNowPlaying = true } label: {
                    HStack(spacing: 12) {
                        ZStack {
                            if let artwork = music.artwork {
                                Image(uiImage: artwork).resizable().aspectRatio(contentMode: .fill)
                            } else {
                                SquareArtwork(item: track)
                            }
                        }
                        .frame(width: 44, height: 44)
                        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                        VStack(alignment: .leading, spacing: 1) {
                            Text(track.title)
                                .font(.subheadline.weight(.semibold))
                                .foregroundStyle(Color.screen)
                            if let artist = track.artistName {
                                Text(artist)
                                    .font(.caption)
                                    .foregroundStyle(Color.usher)
                            }
                        }
                        .lineLimit(1)
                        Spacer(minLength: 0)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(CardButtonStyle())
                if music.isLoading {
                    ProgressView().tint(Color.screen).frame(width: 48, height: 48)
                } else {
                    Button { music.togglePlayPause() } label: {
                        Label(music.isPlaying ? "Pause" : "Play", systemImage: music.isPlaying ? "pause.fill" : "play.fill")
                    }
                    .buttonStyle(RoundActionStyle())
                    .frame(width: 48, height: 48)
                }
                if music.isPodcast {
                    Button { music.skip(by: 30) } label: {
                        Label("Forward 30 seconds", systemImage: "goforward.30")
                    }
                    .buttonStyle(RoundActionStyle())
                    .frame(width: 48, height: 48)
                } else {
                    Button { music.next() } label: {
                        Label("Next", systemImage: "forward.fill")
                    }
                    .buttonStyle(RoundActionStyle())
                    .frame(width: 48, height: 48)
                    .disabled(!music.hasNext)
                    .opacity(music.hasNext ? 1 : 0.4)
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(Color.seat)
                    .overlay(alignment: .bottom) {
                        // How far the track has got, along the bottom edge.
                        GeometryReader { proxy in
                            Capsule().fill(Color.screen.opacity(0.7))
                                .frame(width: max(4, proxy.size.width * (music.duration > 0 ? music.position / music.duration : 0)), height: 2)
                        }
                        .frame(height: 2)
                        .padding(.horizontal, 14)
                        .padding(.bottom, 4)
                    }
                    .shadow(color: .black.opacity(0.35), radius: 12, y: 4)
            }
            .padding(.horizontal, 10)
            .padding(.bottom, 6)
            #if DEBUG
            // `-FlexarrShowNowPlaying YES` opens Now Playing as soon as something plays (with `-FlexarrPlay <id>`).
            .task { if UserDefaults.standard.bool(forKey: "FlexarrShowNowPlaying") { router.showsNowPlaying = true } }
            #endif
            .sheet(isPresented: Bindable(router).showsNowPlaying) {
                NowPlayingView()
                    .presentationDetents([.large])
                    .presentationDragIndicator(.hidden)
                    .presentationBackground(Color.room)
            }
            .transition(.move(edge: .bottom).combined(with: .opacity))
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Now playing: \(track.title)\(track.artistName.map { " by \($0)" } ?? "")")
        }
    }
}
