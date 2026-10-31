import FlexarrKit
import SwiftUI

/// A playlist's picture: the cover chosen for it, or a mosaic of its first tracks' covers.
struct PlaylistArtwork: View {
    @Environment(AppModel.self) private var model
    let playlist: PlaylistSummary
    var size: CGFloat

    var body: some View {
        Group {
            if let cover = playlist.cover {
                RemoteImage(url: model.client?.imageURL(cover, width: size > 400 ? 800 : 500)) { placeholder }
            } else if playlist.covers.count >= 4 {
                let half = size / 2
                VStack(spacing: 0) {
                    HStack(spacing: 0) { tile(playlist.covers[0], half); tile(playlist.covers[1], half) }
                    HStack(spacing: 0) { tile(playlist.covers[2], half); tile(playlist.covers[3], half) }
                }
            } else if let first = playlist.covers.first {
                tile(first, size)
            } else {
                placeholder
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
    }

    private func tile(_ path: String, _ side: CGFloat) -> some View {
        RemoteImage(url: model.client?.imageURL(path, width: side > 300 ? 500 : 300)) { Color.seat }
            .frame(width: side, height: side)
            .clipped()
    }

    private var placeholder: some View {
        ZStack {
            Color.seat
            Image(systemName: "music.note.list")
                .font(.system(size: size * 0.3, weight: .medium))
                .foregroundStyle(Color.usher)
        }
    }
}

/// A playlist in a row: its picture with the name and how many tracks beneath.
struct PlaylistCard: View {
    let playlist: PlaylistSummary
    var width: CGFloat = Layout.posterWidth
    let action: () -> Void

    var body: some View {
        ArtworkButton(ambient: .silver, action: action) {
            PlaylistArtwork(playlist: playlist, size: width)
        } caption: { focused in
            VStack(alignment: .leading, spacing: 4) {
                Text(playlist.name)
                    .font(.system(size: 24, weight: .semibold))
                    .foregroundStyle(focused ? Color.screen : Color.screen.opacity(0.8))
                    .lineLimit(1)
                Text(playlist.trackCountLabel)
                    .font(.system(size: 21))
                    .foregroundStyle(Color.usher)
                    .lineLimit(1)
            }
            .frame(width: width, alignment: .leading)
        }
    }
}

/// A playlist: its picture, name and length, Play and Shuffle, and its tracks in order with the playing one marked.
struct PlaylistView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let playlist: PlaylistSummary
    @State private var detail: PlaylistDetail?
    @State private var error: String?

    private var summary: PlaylistSummary { detail?.summary ?? playlist }
    private var tracks: [ItemSummary] { detail?.tracks.map(\.track) ?? [] }

    var body: some View {
        ZStack {
            RoomBackground(ambient: tracks.first?.glowColor, intensity: 0.35)
                // The whole screen, as a title's backdrop is: left to fill the space it's offered, a screen opened from the
                // sidebar starts beside where the sidebar opens, leaving a bare strip down the left.
                .frame(width: 1920, height: 1080)
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 48) {
                    header
                    if let error, detail == nil {
                        StatusView(state: .failed(error) { Task { await load() } }).frame(height: 300)
                    } else if detail == nil {
                        ProgressView().frame(maxWidth: .infinity).padding(40)
                    } else if tracks.isEmpty {
                        StatusView(state: .empty("Nothing in this playlist yet.")).frame(height: 200)
                    } else {
                        VStack(alignment: .leading, spacing: 6) {
                            ForEach(Array(tracks.enumerated()), id: \.offset) { index, track in
                                TrackRow(track: track, number: index + 1,
                                         isCurrent: model.music.current?.id == track.id, isPlaying: model.music.isPlaying) {
                                    if model.music.current?.id == track.id {
                                        model.music.togglePlayPause()
                                    } else {
                                        model.playTracks(tracks, from: index)
                                    }
                                }
                            }
                        }
                        .frame(maxWidth: 1400)
                        .focusSection()
                    }
                }
                .padding(.horizontal, Layout.edge)
                .padding(.top, 130)
                .padding(.bottom, 90)
            }
            .scrollClipDisabled()
        }
        .task(id: model.refreshToken) { await load() }
    }

    private var header: some View {
        HStack(alignment: .bottom, spacing: 60) {
            PlaylistArtwork(playlist: summary, size: 420)
                .shadow(color: .black.opacity(0.45), radius: 30, y: 16)
            VStack(alignment: .leading, spacing: 20) {
                Text(summary.name)
                    .font(.marquee(72, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .lineLimit(2)
                    .minimumScaleFactor(0.6)
                FactsLine(facts: [summary.trackCountLabel, Format.runtime(seconds: summary.durationSeconds)].compactMap { $0 })
                HStack(spacing: 26) {
                    Button { model.playTracks(tracks, showingNowPlaying: router) } label: {
                        Label("Play", systemImage: "play.fill")
                    }
                    .buttonStyle(ActionButtonStyle(prominent: true))
                    Button { model.playTracks(tracks, shuffled: true, showingNowPlaying: router) } label: {
                        Label("Shuffle", systemImage: "shuffle")
                    }
                    .buttonStyle(ActionButtonStyle())
                    .disabled(tracks.count < 2)
                }
                .disabled(tracks.isEmpty)
                .focusSection()
                .padding(.top, 8)
            }
            Spacer(minLength: 0)
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            detail = try await client.playlist(playlist.id)
            error = nil
        } catch {
            if detail == nil { self.error = ConnectionError(error).message }
        }
    }
}
