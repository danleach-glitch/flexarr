import FlexarrKit
import SwiftUI

/// Everything downloaded (or on its way), playable with no connection: films, shows with their episodes, albums and
/// playlists, then podcast episodes.
struct DownloadsView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var store

    var body: some View {
        Group {
            if store.videoDownloads.isEmpty, store.musicCollections.isEmpty, store.podcastDownloads.isEmpty, store.bookDownloads.isEmpty {
                StatusView(state: .empty("Download films, episodes, albums, playlists and podcasts to watch and listen with no connection. Look for the download button on a title’s page."))
            } else {
                List {
                    let films = store.videoDownloads.filter { $0.item.kind != .episode }
                    if !films.isEmpty {
                        Section("Films") {
                            ForEach(films) { DownloadRow(download: $0) }
                                .onDelete { remove(films, at: $0) }
                        }
                    }
                    ForEach(shows, id: \.title) { show in
                        Section(show.title) {
                            ForEach(show.episodes) { DownloadRow(download: $0) }
                                .onDelete { remove(show.episodes, at: $0) }
                        }
                    }
                    let music = store.musicCollections
                    if !music.isEmpty {
                        Section("Music") {
                            ForEach(music) { CollectionRow(collection: $0) }
                                .onDelete { offsets in for index in offsets { store.remove(collectionId: music[index].id) } }
                        }
                    }
                    let books = store.bookDownloads
                    if !books.isEmpty {
                        Section("Audiobooks") {
                            ForEach(books) { PodcastDownloadRow(download: $0) }
                                .onDelete { offsets in for index in offsets { store.remove(books[index].id) } }
                        }
                    }
                    let podcasts = store.podcastDownloads
                    if !podcasts.isEmpty {
                        Section("Podcasts") {
                            ForEach(podcasts) { PodcastDownloadRow(download: $0) }
                                .onDelete { offsets in for index in offsets { store.remove(podcasts[index].id) } }
                        }
                    }
                    Section {
                        EmptyView()
                    } footer: {
                        Text("\(ByteCountFormatter.string(fromByteCount: store.bytesUsed, countStyle: .file)) used on this \(ThisDevice.name).")
                    }
                }
                .scrollContentBackground(.hidden)
            }
        }
        .background(RoomBackground(ambient: nil))
        .navigationTitle("Downloads")
        .refreshable { await store.catchUp() }
    }

    /// Episodes grouped by show, in episode order.
    private var shows: [(title: String, episodes: [LocalDownload])] {
        let episodes = store.videoDownloads.filter { $0.item.kind == .episode }
        return Dictionary(grouping: episodes) { $0.item.seriesTitle ?? "Episodes" }
            .map { ($0.key, $0.value.sorted { ($0.item.seasonNumber ?? 0, $0.item.episodeNumber ?? 0) < ($1.item.seasonNumber ?? 0, $1.item.episodeNumber ?? 0) }) }
            .sorted { $0.title < $1.title }
    }

    private func remove(_ list: [LocalDownload], at offsets: IndexSet) {
        for index in offsets { store.delete(list[index].id) }
    }
}

/// A download: its picture, title and size, and what it's doing (tap to play once it's here).
private struct DownloadRow: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var store
    let download: LocalDownload

    var body: some View {
        Button(action: play) {
            HStack(spacing: 12) {
                Color.seat
                    .frame(width: 112, height: 63)
                    .overlay {
                        if let name = download.imageFileName, let image = UIImage(contentsOfFile: DownloadStore.folder.appending(path: name).path(percentEncoded: false)) {
                            Image(uiImage: image).resizable().scaledToFill()
                        } else {
                            Image(systemName: "film").foregroundStyle(Color.usher)
                        }
                    }
                    .overlay(alignment: .bottom) {
                        if download.isDownloaded, download.finishedAt == nil, download.durationSeconds > 0, download.position > 30 {
                            ProgressBar(value: download.position / download.durationSeconds).padding(5)
                        }
                    }
                    .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
                VStack(alignment: .leading, spacing: 3) {
                    Text(download.item.kind == .episode ? (download.item.episodeSubtitle ?? download.item.title) : download.item.title)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Color.screen)
                        .lineLimit(2)
                    status
                }
                Spacer(minLength: 0)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!download.isDownloaded)
        .listRowBackground(Color.seat)
        .swipeActions {
            Button(role: .destructive) { store.delete(download.id) } label: { Label("Delete", systemImage: "trash") }
        }
        .accessibilityHint(download.isDownloaded ? "Plays" : "")
    }

    @ViewBuilder private var status: some View {
        switch download.state {
        case _ where store.isWaitingForWiFi(download):
            caption("Waiting for Wi-Fi")
        case .preparing(let progress):
            caption(progress > 0 && progress < 1 ? "Getting ready · \(Int(progress * 100))%" : "Getting ready")
        case .downloading(let progress):
            VStack(alignment: .leading, spacing: 4) {
                caption("Downloading, \(Int(progress * 100))%")
                ProgressView(value: progress).tint(.silver)
            }
        case .downloaded:
            HStack(spacing: 6) {
                DownloadedMark()
                caption([download.quality.label, download.sizeBytes.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) },
                         download.finishedAt != nil ? "Watched" : nil].compactMap { $0 }.joined(separator: "  "))
            }
        case .failed(let message):
            Text(message).font(.caption).foregroundStyle(Color.danger)
        }
    }

    private func caption(_ text: String) -> some View {
        Text(text).font(.caption).foregroundStyle(Color.usher).monospacedDigit()
    }

    private func play() {
        guard let info = store.playbackInfo(for: download, preferences: nil) else { return }
        model.player.playOffline(info)
    }
}

/// A podcast episode or book on the phone: its artwork, title and show (or author), and what it's doing (tap to play
/// once it's here).
private struct PodcastDownloadRow: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var store
    let download: LocalDownload

    private var isBook: Bool { download.item.kind == .audiobook }

    var body: some View {
        Button { if isBook { model.playBook(download.item) } else { model.playEpisode(download.item) } } label: {
            HStack(spacing: 12) {
                Color.seat
                    .frame(width: 63, height: 63)
                    .overlay {
                        if let image = store.image(named: download.imageFileName) {
                            Image(uiImage: image).resizable().scaledToFill()
                        } else {
                            Image(systemName: isBook ? "book.fill" : "mic.fill").foregroundStyle(Color.usher)
                        }
                    }
                    .overlay(alignment: .bottom) {
                        if download.isDownloaded, download.finishedAt == nil, download.durationSeconds > 0, download.position > 0 {
                            ProgressBar(value: min(1, download.position / download.durationSeconds)).padding(5)
                        }
                    }
                    .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
                VStack(alignment: .leading, spacing: 3) {
                    Text(download.item.title)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Color.screen)
                        .lineLimit(2)
                    if let show = isBook ? download.item.bookAuthors : download.item.seriesTitle {
                        Text(show).font(.caption).foregroundStyle(Color.usher).lineLimit(1)
                    }
                    status
                }
                Spacer(minLength: 0)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!download.isDownloaded)
        .listRowBackground(Color.seat)
        .swipeActions {
            Button(role: .destructive) { store.remove(download.id) } label: { Label("Delete", systemImage: "trash") }
        }
    }

    @ViewBuilder private var status: some View {
        switch download.state {
        case _ where store.isWaitingForWiFi(download):
            caption("Waiting for Wi-Fi")
        case .preparing(let progress):
            caption(progress > 0 && progress < 1 ? "Getting ready · \(Int(progress * 100))%" : "Getting ready")
        case .downloading(let progress):
            VStack(alignment: .leading, spacing: 4) {
                caption("Downloading, \(Int(progress * 100))%")
                ProgressView(value: progress).tint(.silver)
            }
        case .downloaded:
            HStack(spacing: 6) {
                DownloadedMark()
                caption([download.sizeBytes.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) },
                     download.finishedAt != nil ? (isBook ? "Finished" : "Played") : nil].compactMap { $0 }.joined(separator: "  "))
            }
        case .failed(let message):
            Text(message).font(.caption).foregroundStyle(Color.danger)
        }
    }

    private func caption(_ text: String) -> some View {
        Text(text).font(.caption).foregroundStyle(Color.usher).monospacedDigit()
    }
}

/// The download button on a title's page: downloads at once, at the size chosen in Settings. Each state looks its
/// own: an hourglass while the server gets it ready, Wi-Fi while it waits for some, a ring only as it comes down, a
/// filled green arrow once here, an exclamation (tap for why) when it failed. Once it's on its way or here, tapping offers to
/// remove it.
struct DownloadButton: View {
    @Environment(DownloadStore.self) private var store
    let item: ItemSummary
    @State private var removing = false
    @State private var error: String?
    @State private var failure: String?

    var body: some View {
        let download = store.entry(for: item.id)
        let waiting = download.map(store.isWaitingForWiFi) ?? false
        Button {
            switch download?.state {
            case nil: Task { await start() }
            case .failed(let reason): failure = reason
            default: removing = true
            }
        } label: {
            Label {
                Text(waiting ? "Waiting for Wi-Fi" : label(download))
            } icon: {
                DownloadStateIcon(state: download?.state, waitingForWiFi: waiting)
            }
        }
        .accessibilityLabel(waiting ? "Waiting for Wi-Fi" : DownloadStateIcon.accessibilityLabel(download?.state))
        .alert("Couldn't download this", isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } })) {
            Button("Try again") {
                if let download { store.delete(download.id) }
                Task { await start() }
            }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(failure ?? "")
        }
        .alert(download?.isDownloaded == true ? "Remove this download?" : "Stop this download?",
               isPresented: $removing) {
            Button(download?.isDownloaded == true ? "Remove download" : "Stop download", role: .destructive) {
                if let download { store.delete(download.id) }
            }
            Button("Cancel", role: .cancel) {}
        }
        .alert("Couldn't download this", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(error ?? "")
        }
    }

    private func start() async {
        do {
            try await store.download(itemId: item.id)
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    private func label(_ download: LocalDownload?) -> String {
        switch download?.state {
        case nil: "Download"
        case .preparing(let progress): progress > 0 && progress < 1 ? "Getting ready · \(Int(progress * 100))%" : "Getting ready"
        case .downloading(let progress): "\(Int(progress * 100))%"
        case .downloaded: "Downloaded"
        case .failed: "Couldn't download"
        }
    }
}

/// What a download is doing, at a glance, the same everywhere: an arrow to download; an hourglass while the server
/// gets it ready; Wi-Fi while it waits for some; a ring that fills only as the file comes down to the phone; a green
/// filled green arrow once it's here; an exclamation when it failed.
struct DownloadStateIcon: View {
    let state: LocalDownload.State?
    var waitingForWiFi = false
    var size: CGFloat = 22
    /// A ring from the start, filling with the server's progress too, where getting ready is part of the wait (podcast
    /// episodes) rather than a separate step to explain.
    var ringWhilePreparing = false

    var body: some View {
        ZStack {
            switch state {
            case _ where waitingForWiFi:
                Image(systemName: "wifi").foregroundStyle(Color.usher)
            case nil:
                Image(systemName: "arrow.down.circle")
            case .preparing(let progress) where ringWhilePreparing:
                DownloadRing(fraction: progress)
            case .preparing:
                Image(systemName: "hourglass").foregroundStyle(Color.usher)
            case .downloading(let fraction):
                DownloadRing(fraction: fraction)
            case .downloaded:
                Image(systemName: "arrow.down.circle.fill")
                    .symbolRenderingMode(.palette)
                    .foregroundStyle(Color.room, Color.go)
            case .failed:
                Image(systemName: "exclamationmark.circle").foregroundStyle(Color.danger)
            }
        }
        .frame(width: size, height: size)
    }

    static func accessibilityLabel(_ state: LocalDownload.State?) -> String {
        switch state {
        case nil: "Download"
        case .preparing: "Getting ready on the server"
        case .downloading(let fraction): "Downloading, \(Int(fraction * 100))%"
        case .downloaded: "Downloaded"
        case .failed: "Couldn't download"
        }
    }
}

/// How much of a file has come down to the phone, as a ring that fills; a stop square in the middle.
struct DownloadRing: View {
    let fraction: Double
    var lineWidth: CGFloat = 2

    var body: some View {
        ZStack {
            Circle().stroke(Color.screen.opacity(0.25), lineWidth: lineWidth)
            Circle().trim(from: 0, to: max(0.02, fraction)).stroke(Color.screen, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .animation(.easeOut(duration: 0.3), value: fraction)
            Image(systemName: "stop.fill").font(.system(size: 7, weight: .bold)).foregroundStyle(Color.screen)
        }
        .padding(1)
    }
}
