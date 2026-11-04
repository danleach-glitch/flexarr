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
    /// Focus starts on Request album, not on the tracks (which only take focus so the remote can scroll): the tracks
    /// arrive with the details, and would otherwise take it from the header as they appear.
    @Namespace private var page
    @FocusState private var focusedAction: MusicActions.Button?
    @State private var status: MusicRequestStatus?
    @Environment(\.musicRequestsPaused) private var pausedAbove

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
            .focusScope(page)
            .environment(\.musicRequestsPaused, paused)
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
                // There from the start (one button whatever it says), so focus has somewhere to land; an album that
                // can't be asked for (or music requests off) has none, and focus goes to its artist or the tracks.
                VStack(alignment: .leading, spacing: 18) {
                    HStack(spacing: 26) {
                        if MusicActions(kind: .album, platform: .tv, hasMusic: false, canAsk: status?.configured != false).has(.request) {
                            MusicAlbumRequestButton(album: album, takesLinkFocus: true, focus: $focusedAction)
                                .prefersDefaultFocus(true, in: page)
                        }
                        if let artist = album.artistPage {
                            Button { router.push(.musicArtist(artist)) } label: {
                                Text(artist.title)
                            }
                            .buttonStyle(PillButtonStyle(selected: false))
                        }
                    }
                    .focusSection()
                    FocusedActionName(name: focusedName)
                }
                .padding(.top, 8)
            }
            Spacer(minLength: 0)
        }
    }

    /// Lidarr is connected but isn't answering (or didn't for this page, so what's asked for is unknown): the button
    /// waits, dimmed.
    private var paused: Bool {
        pausedAbove || (status?.configured == true && (status?.reachable == false || detail?.lidarrAnswered == false))
    }

    /// The round button's name while it's focused; while Lidarr isn't answering, why it's dimmed instead, focused or
    /// not (pressing it does nothing then).
    private var focusedName: String? {
        let state = model.albumState(album)
        if case .inLibrary = state { return nil }
        if case .none = state { return nil }
        if paused { return MusicWords.lidarrNotAnswering }
        guard focusedAction == .request else { return nil }
        if case .requested = state { return MusicWords.requested }
        return MusicWords.requestAlbum
    }

    /// "2001   14 tracks   1h 1m".
    private var facts: [String] {
        var facts: [String] = []
        if let year = album.year { facts.append(String(year)) }
        if let detail {
            if !detail.tracks.isEmpty { facts.append(t("{count, plural, one {# track} other {# tracks}}", ["count": detail.tracks.count])) }
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
                    Text(t("Disc {number}", ["number": disc]))
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
                                Image(systemName: Symbol.explicit)
                                    .font(.system(size: 22))
                                    .accessibilityLabel(t("Explicit"))
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
        async let asked = model.musicRequestStatus()
        do {
            let first = detail == nil
            detail = try await client.musicAlbum(deezerId: preview.deezerId)
            error = nil
            status = await asked
            if first {
                try? await Task.sleep(for: .milliseconds(100))
                focusedAction = .request
            }
        } catch {
            self.error = ConnectionError(error).message
            status = await asked
        }
    }
}
