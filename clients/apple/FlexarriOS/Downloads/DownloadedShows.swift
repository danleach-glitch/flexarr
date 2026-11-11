import FlexarrKit
import SwiftUI

/// A show's episodes on this device, for the Downloads list: one row per show, its episodes on a page of their own.
struct DownloadedShow: Identifiable {
    /// The show's id, or its name from servers that don't send one.
    let id: String
    let title: String
    /// In season and episode order.
    let episodes: [LocalDownload]

    /// The episodes under each season, in order.
    var seasons: [(number: Int?, episodes: [LocalDownload])] {
        var result: [(number: Int?, episodes: [LocalDownload])] = []
        for episode in episodes {
            if let last = result.last, last.number == episode.item.seasonNumber {
                result[result.count - 1].episodes.append(episode)
            } else {
                result.append((episode.item.seasonNumber, [episode]))
            }
        }
        return result
    }
}

extension DownloadStore {
    /// Downloaded (and downloading) episodes grouped by show, shows by name.
    var downloadedShows: [DownloadedShow] {
        let episodes = videoDownloads.filter { $0.item.kind == .episode }
        let order: (LocalDownload) -> (Int, Int) = { ($0.item.seasonNumber ?? 0, $0.item.episodeNumber ?? 0) }
        return Dictionary(grouping: episodes) { $0.item.seriesId ?? $0.item.seriesTitle ?? "" }
            .map { key, list in
                DownloadedShow(id: key, title: list.first?.item.seriesTitle ?? t("Episodes"),
                               episodes: list.sorted { order($0) < order($1) })
            }
            .sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }
    }
}

/// A show in the Downloads list: a picture from one of its episodes, its name and how many episodes are here.
struct DownloadedShowRow: View {
    @Environment(DownloadStore.self) private var store
    @Environment(\.horizontalSizeClass) private var sizeClass
    let show: DownloadedShow
    @State private var removing = false

    var body: some View {
        NavigationLink {
            DownloadedShowView(showId: show.id)
        } label: {
            HStack(spacing: sizeClass == .regular ? 16 : 12) {
                Color.seat
                    .frame(width: sizeClass == .regular ? 192 : 112, height: sizeClass == .regular ? 108 : 63)
                    .overlay {
                        if let image = show.episodes.lazy.compactMap({ store.image(named: $0.imageFileName) }).first {
                            Image(platformImage: image).resizable().scaledToFill()
                        } else {
                            Image(systemName: Symbol.shows).foregroundStyle(Color.usher)
                        }
                    }
                    .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
                VStack(alignment: .leading, spacing: 3) {
                    Text(show.title)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Color.screen)
                        .lineLimit(2)
                    status
                }
                Spacer(minLength: 0)
            }
        }
        .listRowBackground(Color.seat)
        .swipeActions {
            Button(role: .destructive) { removing = true } label: { Label(t(Words.delete), systemImage: Symbol.delete) }
        }
        .alert(t("Remove these downloads?"), isPresented: $removing) {
            Button(t("Remove downloads"), role: .destructive) {
                // Through the group stop, so a Download show or season still asking for them ends too.
                store.delete(episodes: show.episodes.map(\.item), unfinishedOnly: false)
            }
            Button(t(Words.cancel), role: .cancel) {}
        }
    }

    @ViewBuilder private var status: some View {
        let count = t("{count, plural, one {# episode} other {# episodes}}", ["count": show.episodes.count])
        switch GroupDownloadStatus(show.episodes.map(\.state)) {
        case .downloading(let fraction, _, _):
            VStack(alignment: .leading, spacing: 4) {
                caption(count)
                ProgressView(value: fraction).tint(.silver)
            }
        case .downloaded:
            HStack(spacing: 6) {
                DownloadedMark()
                caption([count, formatBytes(show.episodes.compactMap(\.sizeBytes).reduce(0, +))].joined(separator: "  "))
            }
        case .failed(let message):
            VStack(alignment: .leading, spacing: 3) {
                caption(count)
                Text(message).font(.caption).foregroundStyle(Color.danger).lineLimit(1)
            }
        case .notStarted:
            caption(count)
        }
    }

    private func caption(_ text: String) -> some View {
        Text(text).font(.caption).foregroundStyle(Color.usher).monospacedDigit()
    }
}

/// A downloaded show, from the Downloads list: its episodes on this device under each season, playable with no
/// connection.
struct DownloadedShowView: View {
    @Environment(DownloadStore.self) private var store
    let showId: String

    private var show: DownloadedShow? { store.downloadedShows.first { $0.id == showId } }

    var body: some View {
        Group {
            if let show {
                List {
                    ForEach(show.seasons, id: \.number) { season in
                        Section(season.number.map { t("Season {number}", ["number": $0]) } ?? t("Special")) {
                            ForEach(season.episodes) { DownloadRow(download: $0) }
                                // As a group stop, so a Download show or season still asking for it ends.
                                .onDelete { offsets in
                                    store.delete(episodes: offsets.map { season.episodes[$0].item }, unfinishedOnly: false)
                                }
                        }
                    }
                }
                .scrollContentBackground(.hidden)
                .macReadable(title: show.title)
            } else {
                StatusView(state: .empty(t("Nothing downloaded.")))
            }
        }
        .background(RoomBackground(ambient: nil))
        .navigationTitle(show?.title ?? "")
    }
}
