import FlexarrKit
import SwiftUI

/// What a music card or tracklist row is: an artist, album or track from the library, a playlist, or a track in a
/// list it plays from (an album's or playlist's tracklist).
enum MusicMenuTarget {
    case item(ItemSummary)
    case playlist(PlaylistSummary)
    case row(ItemSummary, in: [ItemSummary], at: Int)

    var subject: MusicMenu.Subject {
        switch self {
        case .item(let item):
            switch item.kind {
            case .artist: .artist
            case .album: .album
            default: .track
            }
        case .playlist: .playlist
        case .row: .track
        }
    }

    /// The artist's id, for Go to artist (albums and tracks).
    var artistId: String? {
        switch self {
        case .item(let item), .row(let item, _, _): item.kind == .artist ? nil : item.seriesId
        case .playlist: nil
        }
    }

    /// What Add to playlist adds (the server turns an album or artist into its tracks).
    var addition: PlaylistAdditionRequest? {
        switch self {
        case .item(let item), .row(let item, _, _): PlaylistAdditionRequest(itemId: item.id, title: item.title)
        case .playlist: nil
        }
    }
}

/// Something to add to a playlist, asked for from a menu; the iPhone's tab view shows the sheet.
struct PlaylistAdditionRequest: Identifiable, Hashable {
    let itemId: String
    let title: String
    var id: String { itemId }
}

/// The press-and-hold menu of a music card or tracklist row, in `MusicMenu` order: Play, Shuffle, Play next, Add to
/// queue, Add to playlist (iPhone), Go to artist, Download (iPhone). Tracks are fetched when an action needs them.
struct MusicMenuContent: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    #if os(iOS)
    @Environment(DownloadStore.self) private var downloads
    #endif
    let target: MusicMenuTarget
    /// False on the artist's own page, where Go to artist would go nowhere.
    var showsArtist = true

    private var platform: MusicActions.Platform {
        #if os(tvOS)
        .tv
        #else
        .phone
        #endif
    }

    private var menu: MusicMenu {
        let place: MusicMenu.Place = if case .row = target { .row } else { .card }
        return MusicMenu(subject: target.subject, place: place, platform: platform,
                         hasArtist: showsArtist && target.artistId != nil, canDownload: canDownload)
    }

    var body: some View {
        ForEach(menu.items, id: \.self) { item in
            switch item {
            case .play:
                Button { run { await play(shuffled: false) } } label: { Label("Play", systemImage: Symbol.play) }
            case .shuffle:
                Button { run { await play(shuffled: true) } } label: { Label("Shuffle", systemImage: Symbol.shuffle) }
            case .playNext:
                Button { run { await queue(next: true) } } label: { Label("Play next", systemImage: Symbol.playNext) }
            case .addToQueue:
                Button { run { await queue(next: false) } } label: { Label("Add to queue", systemImage: Symbol.addToQueue) }
            case .addToPlaylist:
                Button { model.addingToPlaylist = target.addition } label: {
                    Label("Add to playlist…", systemImage: Symbol.addToPlaylist)
                }
            case .goToArtist:
                Button { if let id = target.artistId { router.push(.itemID(id)) } } label: {
                    Label("Go to artist", systemImage: Symbol.goToArtist)
                }
            case .download:
                Button { run { await download() } } label: { Label("Download", systemImage: Symbol.download) }
            }
        }
    }

    /// Downloading is for albums and playlists not already kept on this phone.
    private var canDownload: Bool {
        #if os(iOS)
        switch target {
        case .item(let item) where item.kind == .album: downloads.collection(for: item.id) == nil
        case .playlist(let playlist): downloads.collection(for: playlist.id) == nil
        default: false
        }
        #else
        false
        #endif
    }

    private func run(_ work: @escaping @MainActor () async -> Void) {
        Task { await work() }
    }

    /// The tracks the action works on, with the albums they're from where that's known (for Now Playing's names).
    private func tracks() async -> (tracks: [ItemSummary], albums: [ItemSummary]) {
        guard let client = model.client else { return ([], []) }
        do {
            switch target {
            case .item(let item) where item.kind == .track:
                return ([item], [])
            case .item(let item):
                return (try await client.tracks(of: item.id), item.kind == .album ? [item] : [])
            case .playlist(let playlist):
                return (try await client.playlist(playlist.id).tracks.map(\.track), [])
            case .row(let track, _, _):
                return ([track], [])
            }
        } catch {
            model.player.showFailure(ConnectionError(error).message)
            return ([], [])
        }
    }

    private func play(shuffled: Bool) async {
        if case .row(_, let list, let index) = target {
            model.playTracks(list, from: index)
            return
        }
        let found = await tracks()
        model.playTracks(found.tracks, shuffled: shuffled, albums: found.albums, showingNowPlaying: router)
    }

    private func queue(next: Bool) async {
        guard let client = model.client else { return }
        let found = await tracks()
        if next {
            model.music.playNext(found.tracks, client: client)
        } else {
            model.music.enqueue(found.tracks, client: client)
        }
    }

    private func download() async {
        #if os(iOS)
        let found = await tracks()
        guard !found.tracks.isEmpty else { return }
        do {
            switch target {
            case .item(let album): try await downloads.download(album: album, tracks: found.tracks)
            case .playlist(let playlist): try await downloads.download(playlist: playlist, tracks: found.tracks)
            case .row: break
            }
        } catch {
            model.player.showFailure(ConnectionError(error).message)
        }
        #endif
    }
}

/// The ⋯ on an album's or artist's page, holding `MusicActions.menu` for everything the page plays: a menu on the
/// iPhone, and on the TV a button that asks, as the film page's ⋯ does. The caller styles the button.
struct MusicPageMenu: View {
    @Environment(AppModel.self) private var model
    let items: [MusicActions.MenuItem]
    let item: ItemSummary
    /// Every track the page plays, fetched when an action needs them.
    let tracks: () async -> [ItemSummary]
    @State private var asking = false

    var body: some View {
        #if os(tvOS)
        Button { asking = true } label: { Label("More", systemImage: Symbol.more) }
            .accessibilityLabel("More actions")
            .confirmationDialog(item.title, isPresented: $asking) { entries }
        #else
        Menu { entries } label: { Label("More", systemImage: Symbol.more) }
            .accessibilityLabel("More actions")
        #endif
    }

    private var entries: some View {
        ForEach(items, id: \.self) { entry in
            switch entry {
            case .playNext:
                Button { Task { await queue(next: true) } } label: { Label("Play next", systemImage: Symbol.playNext) }
            case .addToQueue:
                Button { Task { await queue(next: false) } } label: { Label("Add to queue", systemImage: Symbol.addToQueue) }
            case .addToPlaylist:
                Button { model.addingToPlaylist = PlaylistAdditionRequest(itemId: item.id, title: item.title) } label: {
                    Label("Add to playlist…", systemImage: Symbol.addToPlaylist)
                }
            case .delete:
                EmptyView()
            }
        }
    }

    private func queue(next: Bool) async {
        guard let client = model.client else { return }
        let found = await tracks()
        if next {
            model.music.playNext(found, client: client)
        } else {
            model.music.enqueue(found, client: client)
        }
    }
}
