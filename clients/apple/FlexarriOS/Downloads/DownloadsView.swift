import FlexarrKit
import SwiftUI

/// Everything downloaded (or on its way), playable with no connection: films, then shows with their episodes.
struct DownloadsView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var store

    var body: some View {
        Group {
            if store.downloads.isEmpty {
                StatusView(state: .empty("Download films and episodes to watch them with no connection. Look for the download button on a title’s page."))
            } else {
                List {
                    let films = store.downloads.filter { $0.item.kind != .episode }
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
        let episodes = store.downloads.filter { $0.item.kind == .episode }
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
                        if let name = download.imageFileName, let image = UIImage(contentsOfFile: DownloadStore.folder.appending(path: name).path()) {
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
                    .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
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
        case .preparing(let progress):
            caption(progress > 0 ? "Getting ready on the server, \(Int(progress * 100))%" : "Waiting for the server")
        case .downloading(let progress):
            VStack(alignment: .leading, spacing: 4) {
                caption("Downloading, \(Int(progress * 100))%")
                ProgressView(value: progress).tint(.silver)
            }
        case .downloaded:
            caption([download.quality.label, download.sizeBytes.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) },
                     download.finishedAt != nil ? "Watched" : nil].compactMap { $0 }.joined(separator: "  "))
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

/// The download button on a title's page: downloads at once, at the size chosen in Settings. Once it's on its way
/// or here, tapping offers to remove it.
struct DownloadButton: View {
    @Environment(DownloadStore.self) private var store
    let item: ItemSummary
    @State private var removing = false
    @State private var error: String?

    var body: some View {
        let download = store.entry(for: item.id)
        Button {
            if download == nil || isFailed(download) {
                Task { await start() }
            } else {
                removing = true
            }
        } label: {
            Label(label(download), systemImage: icon(download))
        }
        .confirmationDialog(download?.isDownloaded == true ? "Remove this download?" : "Stop this download?",
                            isPresented: $removing, titleVisibility: .visible) {
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

    private func isFailed(_ download: LocalDownload?) -> Bool {
        if case .failed = download?.state { true } else { false }
    }

    private func label(_ download: LocalDownload?) -> String {
        switch download?.state {
        case nil: "Download"
        case .preparing: "Preparing"
        case .downloading(let progress): "\(Int(progress * 100))%"
        case .downloaded: "Downloaded"
        case .failed: "Try again"
        }
    }

    private func icon(_ download: LocalDownload?) -> String {
        switch download?.state {
        case nil: "arrow.down"
        case .failed: "exclamationmark.arrow.circlepath"
        case .preparing, .downloading: "arrow.down.circle.dotted"
        case .downloaded: "checkmark.circle.fill"
        }
    }
}
