import FlexarrKit
import SwiftUI

/// A playlist's picture: the cover chosen for it, or a mosaic of its first tracks' covers. Fills the width it's given.
struct PlaylistArtwork: View {
    @Environment(AppModel.self) private var model
    let playlist: PlaylistSummary

    var body: some View {
        Color.seat
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                if let cover = playlist.cover {
                    RemoteImage(url: model.client?.imageURL(cover, width: 500)) { placeholder }
                } else if playlist.covers.count >= 4 {
                    VStack(spacing: 0) {
                        HStack(spacing: 0) { tile(playlist.covers[0]); tile(playlist.covers[1]) }
                        HStack(spacing: 0) { tile(playlist.covers[2]); tile(playlist.covers[3]) }
                    }
                } else if let first = playlist.covers.first {
                    tile(first)
                } else {
                    placeholder
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
    }

    private func tile(_ path: String) -> some View {
        Color.seat.overlay { RemoteImage(url: model.client?.imageURL(path, width: 300)) { Color.seat } }.clipped()
    }

    private var placeholder: some View {
        Image(systemName: "music.note.list")
            .font(.title.weight(.medium))
            .foregroundStyle(Color.usher)
    }
}

/// A playlist in a row: its picture with the name and how many tracks beneath; tapping opens it.
struct PlaylistCard: View {
    @Environment(Router.self) private var router
    let playlist: PlaylistSummary
    var width: CGFloat?

    var body: some View {
        Button { router.push(.playlist(playlist)) } label: {
            VStack(alignment: .leading, spacing: 6) {
                PlaylistArtwork(playlist: playlist)
                VStack(alignment: .leading, spacing: 1) {
                    Text(playlist.name)
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                    Text(playlist.trackCountLabel)
                        .font(.caption)
                        .foregroundStyle(Color.usher)
                }
                .lineLimit(1)
            }
            .frame(width: width)
            .contentShape(Rectangle())
        }
        .buttonStyle(CardButtonStyle())
    }
}

/// A playlist: its picture, name and length, Play, Shuffle and Download, and its tracks in order with the playing
/// one marked and downloaded ones ticked.
struct PlaylistView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @Environment(\.horizontalSizeClass) private var sizeClass
    let playlist: PlaylistSummary
    @State private var detail: PlaylistDetail?
    @State private var error: String?

    private var summary: PlaylistSummary { detail?.summary ?? playlist }
    private var tracks: [ItemSummary] { detail?.tracks.map(\.track) ?? [] }

    var body: some View {
        ZStack {
            RoomBackground(ambient: tracks.first?.glowColor, intensity: 0.25)
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 20) {
                    header
                    if let error, detail == nil {
                        StatusView(state: .failed(error) { Task { await load() } }).frame(height: 200)
                    } else if detail == nil {
                        ProgressView().frame(maxWidth: .infinity).padding(20)
                    } else if tracks.isEmpty {
                        StatusView(state: .empty("Nothing in this playlist yet.")).frame(height: 160)
                    } else {
                        VStack(alignment: .leading, spacing: 0) {
                            ForEach(Array(tracks.enumerated()), id: \.offset) { index, track in
                                TrackRow(track: track, number: index + 1,
                                         isCurrent: model.music.current?.id == track.id, isPlaying: model.music.isPlaying,
                                         isDownloaded: downloads.isDownloaded(trackId: track.id)) {
                                    if model.music.current?.id == track.id {
                                        model.music.togglePlayPause()
                                    } else {
                                        model.playTracks(tracks, from: index)
                                    }
                                }
                            }
                        }
                        .padding(.horizontal, Layout.edge - 8)
                        .frame(maxWidth: 760)
                        .frame(maxWidth: .infinity)
                    }
                }
                .padding(.bottom, 32)
            }
            .refreshable { await load() }
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(.hidden, for: .navigationBar)
        .task(id: model.refreshToken) { await load() }
    }

    private var header: some View {
        VStack(spacing: 16) {
            PlaylistArtwork(playlist: summary)
                .frame(width: sizeClass == .regular ? 320 : 240)
                .shadow(color: .black.opacity(0.45), radius: 20, y: 10)
            VStack(spacing: 6) {
                Text(summary.name)
                    .font(.marquee(sizeClass == .regular ? 40 : 30, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                    .lineLimit(2)
                    .minimumScaleFactor(0.7)
                FactsLine(facts: [summary.trackCountLabel, Format.runtime(seconds: summary.durationSeconds)].compactMap { $0 })
            }
            HStack(spacing: 12) {
                Button { model.playTracks(tracks) } label: {
                    Label("Play", systemImage: "play.fill")
                }
                .buttonStyle(ActionButtonStyle(prominent: true))
                Button { model.playTracks(tracks, shuffled: true) } label: {
                    Label("Shuffle", systemImage: "shuffle")
                }
                .buttonStyle(ActionButtonStyle())
                .disabled(tracks.count < 2)
                MusicDownloadButton(source: .playlist(summary, tracks: tracks))
            }
            .disabled(tracks.isEmpty)
            .fixedSize()
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 12)
        .padding(.horizontal, Layout.edge)
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
