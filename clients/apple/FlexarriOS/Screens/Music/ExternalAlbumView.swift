import FlexarrKit
import SwiftUI

/// An album from outside the library: cover, title, artist (opening their page) and year, Request album, and the
/// tracklist.
struct ExternalAlbumView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    let preview: MusicExternal
    @State private var detail: MusicAlbumDetail?
    @State private var error: String?
    @State private var status: MusicRequestStatus?
    @Environment(\.musicRequestsPaused) private var pausedAbove

    private var album: MusicExternal { detail?.summary ?? preview }

    var body: some View {
        ZStack {
            RoomBackground(ambient: .silver, intensity: 0.3)
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 20) {
                    header
                    if let detail {
                        tracklist(detail)
                    } else if let error {
                        StatusView(state: .failed(error) { Task { await load() } }).frame(height: 240)
                    } else {
                        ProgressView().frame(maxWidth: .infinity).padding(40)
                    }
                }
                .padding(.bottom, 32)
            }
            .refreshable { await load(fresh: true) }
            .environment(\.musicRequestsPaused, paused)
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(.hidden, for: .navigationBar)
        .task { await load() }
    }

    private var header: some View {
        VStack(spacing: 16) {
            ExternalMusicArtwork(image: album.image, title: album.title, imageWidth: 800)
                .frame(width: sizeClass == .regular ? 320 : 240)
            VStack(spacing: 6) {
                Text(album.title)
                    .font(.marquee(sizeClass == .regular ? 40 : 30, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                    .lineLimit(2)
                    .minimumScaleFactor(0.7)
                if let artist = album.artistPage {
                    Button(artist.title) { router.push(.musicArtist(artist)) }
                        .font(.body.weight(.semibold))
                        .foregroundStyle(Color.silver)
                } else if let name = album.artist {
                    Text(name)
                        .font(.body.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                }
                FactsLine(facts: facts)
            }
            if detail != nil {
                if case .inLibrary(let id) = model.albumState(album) {
                    Button { router.push(.musicItem(id: id, isArtist: false)) } label: {
                        Label("In your library", systemImage: Symbol.play)
                    }
                    .buttonStyle(ActionButtonStyle(prominent: true))
                    .fixedSize()
                } else if MusicActions(kind: .album, platform: .phone, hasMusic: false, canAsk: status?.configured == true).has(.request) {
                    // Nothing to play here (`MusicActions`): Request album, or Requested, spelled out as the only button.
                    MusicAlbumRequestButton(album: album, labelled: true)
                    if paused {
                        Text(MusicWords.lidarrNotAnswering)
                            .font(.footnote)
                            .foregroundStyle(Color.usher)
                    }
                }
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 12)
        .padding(.horizontal, Layout.edge)
    }

    /// "2001   14 tracks   1h 1m".
    private var facts: [String] {
        var facts: [String] = []
        if let year = album.year { facts.append(String(year)) }
        if let detail {
            if !detail.tracks.isEmpty { facts.append(detail.tracks.count == 1 ? "1 track" : "\(detail.tracks.count) tracks") }
            if let total = Format.runtime(seconds: Double(detail.durationSeconds)) { facts.append(total) }
        }
        return facts
    }

    private func tracklist(_ detail: MusicAlbumDetail) -> some View {
        let discs = Dictionary(grouping: detail.tracks, by: \.disc).sorted { $0.key < $1.key }
        return VStack(alignment: .leading, spacing: 0) {
            ForEach(discs, id: \.key) { disc, tracks in
                if discs.count > 1 {
                    Text("Disc \(disc)")
                        .font(.marquee(20, weight: .bold))
                        .foregroundStyle(Color.usher)
                        .padding(.horizontal, 12)
                        .padding(.top, disc == discs.first?.key ? 0 : 20)
                        .padding(.bottom, 6)
                }
                ForEach(Array(tracks.enumerated()), id: \.offset) { offset, track in
                    ExternalTrackRow(track: track, number: track.number > 0 ? track.number : offset + 1)
                }
            }
        }
        .padding(.horizontal, Layout.edge - 8)
        .frame(maxWidth: 760)
        .frame(maxWidth: .infinity)
    }

    /// Lidarr is connected but isn't answering (or didn't for this page, so what's asked for is unknown): the buttons
    /// wait, greyed.
    private var paused: Bool {
        pausedAbove || (status?.configured == true && (status?.reachable == false || detail?.lidarrAnswered == false))
    }

    private func load(fresh: Bool = false) async {
        guard let client = model.client, preview.hasPage else { return }
        async let asked = model.musicRequestStatus(fresh: fresh)
        do {
            detail = try await client.musicAlbum(deezerId: preview.deezerId)
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
        status = await asked
    }
}

/// A track of an album the library doesn't have: number, title and length (nothing to play).
private struct ExternalTrackRow: View {
    let track: MusicTrack
    let number: Int

    var body: some View {
        HStack(spacing: 14) {
            Text(String(number))
                .font(.subheadline.weight(.medium))
                .monospacedDigit()
                .foregroundStyle(Color.usher)
                .frame(width: 28, alignment: .trailing)
            Text(track.title)
                .font(.body)
                .foregroundStyle(Color.screen)
                .lineLimit(1)
            if track.explicit {
                Image(systemName: Symbol.explicit)
                    .font(.caption)
                    .foregroundStyle(Color.usher)
                    .accessibilityLabel("Explicit")
            }
            Spacer(minLength: 12)
            if track.durationSeconds > 0 {
                Text(Format.timestamp(Double(track.durationSeconds)))
                    .font(.subheadline)
                    .monospacedDigit()
                    .foregroundStyle(Color.usher)
            }
        }
        .padding(.vertical, 12)
        .padding(.horizontal, 12)
    }
}
