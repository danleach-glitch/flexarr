import FlexarrKit
import SwiftUI

/// An album: cover, title, artist and year, Play and Shuffle, and the tracklist (split by disc when there's more
/// than one) with the playing track marked.
struct AlbumView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @State private var page: MusicPageModel
    /// A track to start focus on (opened from a search result or a deep link).
    var highlighted: String?
    @Environment(\.scenePhase) private var scenePhase
    /// The focused round button, named under the row.
    @FocusState private var focusedAction: MusicActions.Button?

    init(id: String, preview: ItemSummary?, highlighted: String? = nil) {
        _page = State(initialValue: MusicPageModel(id: id, preview: preview))
        self.highlighted = highlighted
    }

    private var ambient: Color { page.item?.glowColor ?? .silver }
    private var tracks: [ItemSummary] { page.tracks }

    var body: some View {
        ZStack {
            RoomBackground(ambient: ambient, intensity: 0.4)
                // The whole screen, as a title's backdrop is: left to fill the space it's offered, a screen opened from the
                // sidebar starts beside where the sidebar opens, leaving a bare strip down the left.
                .frame(width: 1920, height: 1080)
            if let item = page.item {
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 48) {
                        header(item)
                        tracklist
                    }
                    .padding(.horizontal, Layout.edge)
                    .padding(.top, 130)
                    .padding(.bottom, 90)
                }
                .scrollClipDisabled()
            } else if let error = page.error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
            }
        }
        .task(id: model.refreshToken) { await load(ifFresh: true) }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active { Task { await load(ifFresh: true) } }
        }
    }

    private func header(_ item: ItemSummary) -> some View {
        HStack(alignment: .bottom, spacing: 60) {
            SquareArtwork(item: item, size: 420)
            VStack(alignment: .leading, spacing: 20) {
                Text(item.title)
                    .font(.marquee(72, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .lineLimit(2)
                    .minimumScaleFactor(0.6)
                if let artist = page.artist {
                    Button { router.push(.item(artist)) } label: {
                        Text(artist.title)
                    }
                    .buttonStyle(PillButtonStyle(selected: false, ambient: ambient))
                } else if let name = item.artistName {
                    Text(name)
                        .font(.system(size: 30, weight: .semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                }
                FactsLine(facts: facts(item))
                // In `MusicActions` order: Play spelled out, the rest round and named underneath while focused (as on an
                // artist's page).
                VStack(alignment: .leading, spacing: 18) {
                    HStack(spacing: 26) {
                        ForEach(actions.buttons, id: \.self) { button in
                            actionButton(button, item: item)
                        }
                    }
                    .disabled(tracks.isEmpty)
                    .focusSection()
                    FocusedActionName(name: focusedAction == .shuffle ? "Shuffle" : focusedAction == .more ? "More" : nil)
                }
                .padding(.top, 8)
            }
            Spacer(minLength: 0)
        }
    }

    /// An album in the library: Play, Shuffle and ⋯ (no Download on the TV).
    private var actions: MusicActions {
        MusicActions(kind: .album, platform: .tv, hasMusic: true, canAsk: false, canQueue: true)
    }

    @ViewBuilder
    private func actionButton(_ button: MusicActions.Button, item: ItemSummary) -> some View {
        switch button {
        case .play:
            Button { model.playTracks(tracks, albums: [item], showingNowPlaying: router) } label: {
                Label("Play", systemImage: Symbol.play)
            }
            .buttonStyle(ActionButtonStyle(ambient: page.item?.ambientColor, prominent: true))
            .takesFocusFromLink()
        case .shuffle:
            Button { model.playTracks(tracks, shuffled: true, albums: [item], showingNowPlaying: router) } label: {
                Label("Shuffle", systemImage: Symbol.shuffle)
            }
            .buttonStyle(ActionButtonStyle(ambient: ambient, iconOnly: true))
            .focused($focusedAction, equals: .shuffle)
            .disabled(tracks.count < 2)
        case .more:
            MusicPageMenu(items: actions.menu, item: item) { tracks }
                .buttonStyle(ActionButtonStyle(ambient: ambient, iconOnly: true))
                .focused($focusedAction, equals: .more)
                .disabled(tracks.isEmpty)
        default:
            EmptyView()
        }
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
                StatusView(state: .empty("No tracks yet.")).frame(height: 200)
            }
        } else {
            let discs = ItemSummary.discs(of: tracks)
            VStack(alignment: .leading, spacing: 6) {
                ForEach(discs, id: \.disc) { disc in
                    if discs.count > 1 {
                        Text("Disc \(disc.disc)")
                            .font(.marquee(34, weight: .bold))
                            .foregroundStyle(Color.usher)
                            .padding(.leading, 30)
                            .padding(.top, disc.disc == discs.first?.disc ? 0 : 30)
                            .padding(.bottom, 10)
                    }
                    ForEach(Array(disc.tracks.enumerated()), id: \.element.id) { offset, track in
                        TrackRow(track: track, number: track.trackNumber ?? offset + 1,
                                 isCurrent: model.music.current?.id == track.id, isPlaying: model.music.isPlaying,
                                 subtitle: track.trackArtist,
                                 menu: tracks.firstIndex(of: track).map { .row(track, in: tracks, at: $0) },
                                 offersArtist: false) {
                            if model.music.current?.id == track.id {
                                model.music.togglePlayPause()
                            } else if let index = tracks.firstIndex(of: track) {
                                model.playTracks(tracks, from: index, albums: [page.item].compactMap { $0 })
                            }
                        }
                        .prefersDefaultFocus(track.id == highlighted, in: trackFocus)
                    }
                }
            }
            .frame(maxWidth: 1400)
            .focusSection()
            .focusScope(trackFocus)
        }
    }

    @Namespace private var trackFocus

    private func load(ifFresh: Bool = false) async {
        guard let client = model.client else { return }
        await page.load(client: client, onlyIfStale: ifFresh)
    }
}
