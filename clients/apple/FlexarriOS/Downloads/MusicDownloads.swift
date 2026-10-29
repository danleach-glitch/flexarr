import FlexarrKit
import SwiftUI

/// What a music download button or row stands for: an album with its tracks, or a playlist with its.
enum MusicDownloadSource {
    case album(ItemSummary, tracks: [ItemSummary])
    case playlist(PlaylistSummary, tracks: [ItemSummary])

    var id: String {
        switch self {
        case .album(let album, _): album.id
        case .playlist(let playlist, _): playlist.id
        }
    }

    var tracks: [ItemSummary] {
        switch self {
        case .album(_, let tracks), .playlist(_, let tracks): tracks
        }
    }
}

/// The round download button beside Play and Shuffle on an album or playlist: downloads every track at once; while
/// they come, a ring fills; once here, a filled green arrow. Tapping again offers to remove it (or to stop it).
struct MusicDownloadButton: View {
    @Environment(DownloadStore.self) private var store
    let source: MusicDownloadSource
    @State private var removing = false
    @State private var error: String?
    @State private var failure: String?

    var body: some View {
        let collection = store.collection(for: source.id)
        let status = collection.map(store.status(of:))
        Button {
            switch status {
            case nil: Task { await start() }
            case .failed(let reason): failure = reason
            default: removing = true
            }
        } label: {
            ZStack {
                switch status {
                case .downloading(let fraction):
                    DownloadRing(fraction: fraction, lineWidth: 3)
                case .preparing:
                    Image(systemName: "hourglass").foregroundStyle(Color.usher)
                case .downloaded:
                    Image(systemName: "checkmark").foregroundStyle(Color.downloaded)
                case .failed:
                    Image(systemName: "exclamationmark.circle").foregroundStyle(Color.danger)
                case nil:
                    Image(systemName: "arrow.down")
                }
            }
            .frame(width: 22, height: 22)
        }
        .buttonStyle(RoundActionStyle())
        .disabled(source.tracks.isEmpty)
        .accessibilityLabel(label(status))
        .confirmationDialog(status == .downloaded ? "Remove this download?" : "Stop this download?", isPresented: $removing, titleVisibility: .visible) {
            Button(status == .downloaded ? "Remove download" : "Stop download", role: .destructive) { store.remove(collectionId: source.id) }
            Button("Cancel", role: .cancel) {}
        } message: {
            if let collection, status == .downloaded {
                Text("\(ByteCountFormatter.string(fromByteCount: store.bytesUsed(by: collection), countStyle: .file)) on this \(ThisDevice.name).")
            }
        }
        .alert("Couldn't download this", isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } })) {
            Button("Try again") { Task { await start() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(failure ?? "")
        }
        .alert("Couldn't download this", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(error ?? "")
        }
    }

    private func start() async {
        do {
            switch source {
            case .album(let album, let tracks): try await store.download(album: album, tracks: tracks)
            case .playlist(let playlist, let tracks): try await store.download(playlist: playlist, tracks: tracks)
            }
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    private func label(_ status: LocalCollection.Status?) -> String {
        switch status {
        case nil: "Download"
        case .preparing: "Getting ready on the server"
        case .downloading(let fraction): "Downloading, \(Int(fraction * 100))%"
        case .downloaded: "Downloaded"
        case .failed: "Couldn't download"
        }
    }
}

/// A small filled green arrow that marks something downloaded (a track, an album, a film, an episode).
struct DownloadedMark: View {
    var body: some View {
        Image(systemName: "arrow.down.circle.fill")
            .font(.caption.weight(.semibold))
            .symbolRenderingMode(.palette)
            .foregroundStyle(Color.room, Color.downloaded)
            .accessibilityLabel("Downloaded")
    }
}

/// An album's or playlist's saved picture: its cover, or for a playlist without one a mosaic of its tracks' covers.
struct CollectionArtwork: View {
    @Environment(DownloadStore.self) private var store
    let collection: LocalCollection

    var body: some View {
        Color.seat
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                if let image = store.image(named: collection.imageFileName) {
                    Image(uiImage: image).resizable().scaledToFill()
                } else {
                    let names = store.tracks(in: collection).compactMap(\.imageFileName).reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }
                    let covers = names.compactMap { store.image(named: $0) }
                    if covers.count >= 4 {
                        VStack(spacing: 0) {
                            HStack(spacing: 0) { tile(covers[0]); tile(covers[1]) }
                            HStack(spacing: 0) { tile(covers[2]); tile(covers[3]) }
                        }
                    } else if let first = covers.first {
                        Image(uiImage: first).resizable().scaledToFill()
                    } else {
                        Image(systemName: collection.kind == .playlist ? "music.note.list" : "music.note")
                            .font(.title2.weight(.medium))
                            .foregroundStyle(Color.usher)
                    }
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
    }

    private func tile(_ image: UIImage) -> some View {
        Color.seat.overlay { Image(uiImage: image).resizable().scaledToFill() }.clipped()
    }
}

/// An album or playlist in the Downloads list: its picture, name and what it's doing; tapping opens it.
struct CollectionRow: View {
    @Environment(DownloadStore.self) private var store
    let collection: LocalCollection

    var body: some View {
        NavigationLink {
            OfflineCollectionView(collectionId: collection.id)
        } label: {
            HStack(spacing: 12) {
                CollectionArtwork(collection: collection).frame(width: 63, height: 63)
                VStack(alignment: .leading, spacing: 3) {
                    Text(collection.title)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Color.screen)
                        .lineLimit(2)
                    if let subtitle = collection.subtitle {
                        Text(subtitle).font(.caption).foregroundStyle(Color.usher).lineLimit(1)
                    }
                    status
                }
                Spacer(minLength: 0)
            }
        }
        .listRowBackground(Color.seat)
        .swipeActions {
            Button(role: .destructive) { store.remove(collectionId: collection.id) } label: { Label("Delete", systemImage: "trash") }
        }
    }

    @ViewBuilder private var status: some View {
        let tracks = store.tracks(in: collection)
        let here = tracks.filter(\.isDownloaded).count
        switch store.status(of: collection) {
        case .preparing:
            caption(["Getting ready", estimate].compactMap { $0 }.joined(separator: "  "))
        case .downloading(let fraction):
            VStack(alignment: .leading, spacing: 4) {
                caption(["\(here) of \(collection.trackIds.count) tracks", estimate].compactMap { $0 }.joined(separator: "  "))
                ProgressView(value: fraction).tint(.silver)
            }
        case .downloaded:
            HStack(spacing: 6) {
                DownloadedMark()
                caption([collection.trackIds.count == 1 ? "1 track" : "\(collection.trackIds.count) tracks",
                         ByteCountFormatter.string(fromByteCount: store.bytesUsed(by: collection), countStyle: .file)].joined(separator: "  "))
            }
        case .failed(let message):
            Text(message).font(.caption).foregroundStyle(Color.danger)
        }
    }

    private var estimate: String? {
        collection.estimatedBytes.map { "about " + ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) }
    }

    private func caption(_ text: String) -> some View {
        Text(text).font(.caption).foregroundStyle(Color.usher).monospacedDigit()
    }
}

/// A downloaded album or playlist, from the Downloads tab: its saved cover, Play and Shuffle, and its tracks (the
/// ones here playable), all with no connection.
struct OfflineCollectionView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(DownloadStore.self) private var store
    @Environment(\.dismiss) private var dismiss
    @Environment(\.horizontalSizeClass) private var sizeClass
    let collectionId: String
    @State private var removing = false

    private var collection: LocalCollection? { store.collection(for: collectionId) }

    var body: some View {
        ZStack {
            if let collection {
                let downloads = store.tracks(in: collection)
                let tracks = downloads.filter(\.isDownloaded).map(\.item)
                RoomBackground(ambient: ambient(collection), intensity: 0.3)
                ScrollView(.vertical) {
                    VStack(alignment: .leading, spacing: 20) {
                        header(collection, tracks: tracks)
                        if downloads.isEmpty {
                            StatusView(state: .empty("Nothing here yet.")).frame(height: 160)
                        } else {
                            VStack(alignment: .leading, spacing: 0) {
                                ForEach(Array(downloads.enumerated()), id: \.element.id) { offset, download in
                                    let track = download.item
                                    TrackRow(track: track, number: collection.kind == .album ? (track.trackNumber ?? offset + 1) : offset + 1,
                                             isCurrent: model.music.current?.id == track.id, isPlaying: model.music.isPlaying,
                                             isDownloaded: download.isDownloaded, isDimmed: !download.isDownloaded) {
                                        if model.music.current?.id == track.id {
                                            model.music.togglePlayPause()
                                        } else if let index = tracks.firstIndex(of: track) {
                                            model.playTracks(tracks, from: index, albums: [collection.album].compactMap { $0 })
                                        }
                                    }
                                    .disabled(!download.isDownloaded)
                                }
                            }
                            .padding(.horizontal, Layout.edge - 8)
                            .frame(maxWidth: 760)
                            .frame(maxWidth: .infinity)
                        }
                    }
                    .padding(.bottom, 32)
                }
            } else {
                StatusView(state: .empty("This download was removed."))
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbarBackground(.hidden, for: .navigationBar)
        .toolbar {
            if collection != nil {
                Button { removing = true } label: { Label("Remove download", systemImage: "trash") }
            }
        }
        .confirmationDialog("Remove this download?", isPresented: $removing, titleVisibility: .visible) {
            Button("Remove download", role: .destructive) {
                store.remove(collectionId: collectionId)
                dismiss()
            }
            Button("Cancel", role: .cancel) {}
        }
    }

    private func ambient(_ collection: LocalCollection) -> Color {
        collection.album?.glowColor ?? collection.accentColor.flatMap(RGB.init(hex:)).map(Color.init(rgb:)) ?? .silver
    }

    private func header(_ collection: LocalCollection, tracks: [ItemSummary]) -> some View {
        VStack(spacing: 16) {
            CollectionArtwork(collection: collection)
                .frame(width: sizeClass == .regular ? 320 : 240)
                .shadow(color: .black.opacity(0.45), radius: 20, y: 10)
            VStack(spacing: 6) {
                Text(collection.title)
                    .font(.marquee(sizeClass == .regular ? 40 : 30, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                    .lineLimit(2)
                    .minimumScaleFactor(0.7)
                if let subtitle = collection.subtitle {
                    Text(subtitle)
                        .font(.body.weight(.semibold))
                        .foregroundStyle(Color.screen.opacity(0.9))
                }
                FactsLine(facts: facts(collection, tracks: tracks))
            }
            HStack(spacing: 12) {
                Button { model.playTracks(tracks, albums: [collection.album].compactMap { $0 }, showingNowPlaying: router) } label: {
                    Label("Play", systemImage: "play.fill")
                }
                .buttonStyle(ActionButtonStyle(ambient: ambient(collection), prominent: true))
                Button { model.playTracks(tracks, shuffled: true, albums: [collection.album].compactMap { $0 }, showingNowPlaying: router) } label: {
                    Label("Shuffle", systemImage: "shuffle")
                }
                .buttonStyle(ActionButtonStyle(ambient: ambient(collection)))
                .disabled(tracks.count < 2)
            }
            .disabled(tracks.isEmpty)
            .fixedSize()
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 12)
        .padding(.horizontal, Layout.edge)
    }

    /// "1985   9 tracks   41m   Downloaded", or how many are here so far.
    private func facts(_ collection: LocalCollection, tracks: [ItemSummary]) -> [String] {
        var facts: [String] = []
        if let year = collection.album?.releaseYear { facts.append(String(year)) }
        let total = collection.trackIds.count
        facts.append(total == 1 ? "1 track" : "\(total) tracks")
        if let runtime = ItemSummary.totalRuntime(of: tracks) { facts.append(runtime) }
        facts.append(tracks.count == total ? "Downloaded" : "\(tracks.count) here")
        return facts
    }
}
