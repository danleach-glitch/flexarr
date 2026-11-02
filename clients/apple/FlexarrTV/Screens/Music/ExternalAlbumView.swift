import FlexarrKit
import SwiftUI

/// An album from outside the library: cover, title, artist (opening their page) and year, Request album, and the
/// tracklist.
struct ExternalAlbumView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let preview: MusicExternal
    @State private var detail: MusicAlbumDetail?
    @State private var error: String?

    private var album: MusicExternal { detail?.summary ?? preview }

    var body: some View {
        ZStack {
            RoomBackground(ambient: .silver, intensity: 0.4)
                .frame(width: 1920, height: 1080)
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 48) {
                    header
                    if let detail {
                        tracklist(detail)
                    } else if let error {
                        StatusView(state: .failed(error) { Task { await load() } }).frame(height: 300)
                    } else {
                        StatusView(state: .loading).frame(height: 300)
                    }
                }
                .padding(.horizontal, Layout.edge)
                .padding(.top, 130)
                .padding(.bottom, 90)
            }
            .scrollClipDisabled()
        }
        .task { await load() }
    }

    private var header: some View {
        HStack(alignment: .bottom, spacing: 60) {
            ExternalMusicArtwork(image: album.image, title: album.title, size: 420)
            VStack(alignment: .leading, spacing: 20) {
                Text(album.title)
                    .font(.marquee(72, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .lineLimit(2)
                    .minimumScaleFactor(0.6)
                if let name = album.artist {
                    Text(name)
                        .font(.system(size: 30, weight: .semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                }
                FactsLine(facts: facts)
                // There from the start (one button whatever it says), so focus has somewhere to land.
                HStack(spacing: 26) {
                    MusicAlbumRequestButton(album: album)
                        .takesFocusFromLink()
                    if let artist = album.artistPage {
                        Button { router.push(.musicArtist(artist)) } label: {
                            Text(artist.title)
                        }
                        .buttonStyle(PillButtonStyle(selected: false))
                    }
                }
                .focusSection()
                .padding(.top, 8)
            }
            Spacer(minLength: 0)
        }
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

    /// Focusable rows, though nothing plays: the remote can only scroll down a page by moving focus.
    private func tracklist(_ detail: MusicAlbumDetail) -> some View {
        let discs = Dictionary(grouping: detail.tracks, by: \.disc).sorted { $0.key < $1.key }
        return VStack(alignment: .leading, spacing: 6) {
            ForEach(discs, id: \.key) { disc, tracks in
                if discs.count > 1 {
                    Text("Disc \(disc)")
                        .font(.marquee(34, weight: .bold))
                        .foregroundStyle(Color.usher)
                        .padding(.leading, 30)
                        .padding(.top, disc == discs.first?.key ? 0 : 30)
                        .padding(.bottom, 10)
                }
                ForEach(Array(tracks.enumerated()), id: \.offset) { offset, track in
                    Button {} label: {
                        HStack(spacing: 28) {
                            Text(String(track.number > 0 ? track.number : offset + 1))
                                .font(.system(size: 26, weight: .medium))
                                .monospacedDigit()
                                .frame(width: 56, alignment: .trailing)
                            Text(track.title)
                                .font(.system(size: 28, weight: .medium))
                                .lineLimit(1)
                            if track.explicit {
                                Image(systemName: "e.square.fill")
                                    .font(.system(size: 22))
                                    .accessibilityLabel("Explicit")
                            }
                            Spacer(minLength: 20)
                            if track.durationSeconds > 0 {
                                Text(Format.timestamp(Double(track.durationSeconds)))
                                    .font(.system(size: 24, weight: .medium))
                                    .monospacedDigit()
                            }
                        }
                    }
                    .buttonStyle(TrackRowStyle(isCurrent: false))
                }
            }
        }
        .frame(maxWidth: 1400)
        .focusSection()
    }

    private func load() async {
        guard let client = model.client, preview.hasPage else { return }
        do {
            detail = try await client.musicAlbum(deezerId: preview.deezerId)
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}
