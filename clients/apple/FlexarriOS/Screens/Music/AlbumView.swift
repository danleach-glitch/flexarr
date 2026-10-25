import FlexarrKit
import SwiftUI

/// An album: cover, title, artist and year, Play, Shuffle and Download, and the tracklist (split by disc when
/// there's more than one) with the playing track marked and downloaded ones ticked.
struct AlbumView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(DownloadStore.self) private var downloads
    @State private var page: MusicPageModel
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.horizontalSizeClass) private var sizeClass

    init(id: String, preview: ItemSummary?) {
        _page = State(initialValue: MusicPageModel(id: id, preview: preview))
    }

    private var ambient: Color { page.item?.glowColor ?? .silver }
    private var tracks: [ItemSummary] { page.tracks }

    var body: some View {
        ZStack {
            RoomBackground(ambient: ambient, intensity: 0.3)
            if let item = page.item {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 20) {
                        header(item)
                        tracklist
                    }
                    .padding(.bottom, 32)
                }
                .refreshable { await load() }
            } else if let error = page.error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(.hidden, for: .navigationBar)
        .task(id: model.refreshToken) { await load(ifFresh: true) }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active { Task { await load(ifFresh: true) } }
        }
    }

    private func header(_ item: ItemSummary) -> some View {
        VStack(spacing: 16) {
            SquareArtwork(item: item, imageWidth: 800)
                .frame(width: sizeClass == .regular ? 320 : 240)
                .shadow(color: .black.opacity(0.45), radius: 20, y: 10)
            VStack(spacing: 6) {
                Text(item.title)
                    .font(.marquee(sizeClass == .regular ? 40 : 30, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                    .lineLimit(2)
                    .minimumScaleFactor(0.7)
                if let artist = page.artist {
                    Button(artist.title) { router.push(.item(artist)) }
                        .font(.body.weight(.semibold))
                        .foregroundStyle(Color.silver)
                } else if let name = item.artistName {
                    Text(name)
                        .font(.body.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                }
                FactsLine(facts: facts(item))
            }
            HStack(spacing: 12) {
                Button { model.playTracks(tracks, albums: [item]) } label: {
                    Label("Play", systemImage: "play.fill")
                }
                .buttonStyle(ActionButtonStyle(ambient: ambient, prominent: true))
                Button { model.playTracks(tracks, shuffled: true, albums: [item]) } label: {
                    Label("Shuffle", systemImage: "shuffle")
                }
                .buttonStyle(ActionButtonStyle(ambient: ambient))
                .disabled(tracks.count < 2)
                MusicDownloadButton(source: .album(item, tracks: tracks))
            }
            .disabled(tracks.isEmpty)
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 12)
        .padding(.horizontal, Layout.edge)
    }

    /// "1991   12 tracks   46m".
    private func facts(_ item: ItemSummary) -> [String] {
        var facts: [String] = []
        if let year = item.releaseYear { facts.append(String(year)) }
        let count = tracks.isEmpty ? item.childCount : tracks.count
        if count > 0 { facts.append(count == 1 ? "1 track" : "\(count) tracks") }
        if let total = ItemSummary.totalRuntime(of: tracks) { facts.append(total) }
        return facts
    }

    @ViewBuilder
    private var tracklist: some View {
        if tracks.isEmpty {
            if page.detail != nil {
                StatusView(state: .empty("No tracks yet.")).frame(height: 160)
            }
        } else {
            let discs = ItemSummary.discs(of: tracks)
            VStack(alignment: .leading, spacing: 0) {
                ForEach(discs, id: \.disc) { disc in
                    if discs.count > 1 {
                        Text("Disc \(disc.disc)")
                            .font(.marquee(20, weight: .bold))
                            .foregroundStyle(Color.usher)
                            .padding(.horizontal, 12)
                            .padding(.top, disc.disc == discs.first?.disc ? 0 : 20)
                            .padding(.bottom, 6)
                    }
                    ForEach(Array(disc.tracks.enumerated()), id: \.element.id) { offset, track in
                        TrackRow(track: track, number: track.trackNumber ?? offset + 1,
                                 isCurrent: model.music.current?.id == track.id, isPlaying: model.music.isPlaying,
                                 isDownloaded: downloads.isDownloaded(trackId: track.id)) {
                            if model.music.current?.id == track.id {
                                model.music.togglePlayPause()
                            } else if let index = tracks.firstIndex(of: track) {
                                model.playTracks(tracks, from: index, albums: [page.item].compactMap { $0 })
                            }
                        }
                    }
                }
            }
            .padding(.horizontal, Layout.edge - 8)
            .frame(maxWidth: 760)
            .frame(maxWidth: .infinity)
        }
    }

    private func load(ifFresh: Bool = false) async {
        guard let client = model.client else { return }
        await page.load(client: client, onlyIfStale: ifFresh)
    }
}
