import FlexarrKit
import SwiftUI

/// Where a group of episodes (a season, a whole show) stands on this device, from their downloads.
enum GroupDownloadStatus: Equatable {
    /// Some (or all) aren't here or on their way, and nothing is coming down.
    case notStarted(missing: Int)
    /// Some are on their way: how far the group has got on average, and how many are here.
    case downloading(fraction: Double, done: Int, total: Int)
    /// Every one is here.
    case downloaded
    /// One failed (the first one's reason).
    case failed(String)

    init(_ states: [LocalDownload.State?]) {
        if let failure = states.lazy.compactMap({ state -> String? in if case .failed(let reason) = state { reason } else { nil } }).first {
            self = .failed(failure)
            return
        }
        let done = states.filter { $0 == .downloaded }.count
        let inFlight = states.contains { state in
            switch state {
            case .preparing, .downloading: true
            default: false
            }
        }
        if inFlight {
            let fractions = states.map { state -> Double in
                switch state {
                case .downloaded: 1
                case .downloading(let fraction): fraction
                default: 0
                }
            }
            self = .downloading(fraction: fractions.reduce(0, +) / Double(max(1, fractions.count)), done: done, total: states.count)
        } else if done == states.count, !states.isEmpty {
            self = .downloaded
        } else {
            self = .notStarted(missing: states.count - done)
        }
    }

    /// The icon `DownloadStateIcon` draws for it.
    var iconState: LocalDownload.State? {
        switch self {
        case .notStarted: nil
        case .downloading(let fraction, _, _): .downloading(fraction)
        case .downloaded: .downloaded
        case .failed(let reason): .failed(reason)
        }
    }
}

extension DownloadStore {
    /// Each episode's download state here (nil: not downloaded), in order.
    func states(of episodes: [ItemSummary]) -> [LocalDownload.State?] {
        episodes.map { entry(for: $0.id)?.state }
    }

    /// Downloads every one of these not already here or on its way (a failed one starts again), one after another,
    /// so the server queues them in order. Carries on past a failure and throws the first one at the end.
    func download(all episodes: [ItemSummary]) async throws {
        var firstError: Error?
        for episode in episodes {
            if let existing = entry(for: episode.id) {
                if case .failed = existing.state { delete(existing.id) } else { continue }
            }
            do { try await download(itemId: episode.id) } catch { firstError = firstError ?? error }
        }
        if let firstError { throw firstError }
    }

    /// Deletes these episodes' downloads: all of them, or only those still on their way.
    func delete(episodes: [ItemSummary], unfinishedOnly: Bool) {
        for episode in episodes {
            guard let download = entry(for: episode.id) else { continue }
            if unfinishedOnly && download.isDownloaded { continue }
            delete(download.id)
        }
    }

    /// Whether anything of this show is downloaded or on its way here.
    func hasDownloads(ofShow showId: String) -> Bool {
        all.contains { $0.profileId == profileId && $0.item.seriesId == showId }
    }
}

/// What tapping a group's download control does, and the questions it asks: download what's missing (a whole show
/// asks first, with the count), stop what's on its way, remove what's here, or try a failure again.
private struct GroupDownloadActions: ViewModifier {
    @Environment(DownloadStore.self) private var store
    let episodes: [ItemSummary]?
    @Binding var trigger: Bool
    /// Asks before downloading, naming the show (a whole show can be a lot); nil downloads straight away.
    let confirmsFor: String?
    @State private var confirming = 0
    @State private var stopping = false
    @State private var removing = false
    @State private var error: String?

    func body(content: Content) -> some View {
        content
            .onChange(of: trigger) { _, pressed in
                guard pressed else { return }
                trigger = false
                guard let episodes else { return }
                switch GroupDownloadStatus(store.states(of: episodes)) {
                case .notStarted(let missing):
                    if confirmsFor != nil { confirming = missing } else { start(episodes) }
                case .failed: start(episodes)
                case .downloading: stopping = true
                case .downloaded: removing = true
                }
            }
            .alert(t("Download {count, plural, one {# episode} other {# episodes}} of {show}?", ["count": confirming, "show": confirmsFor ?? ""]),
                   isPresented: Binding(get: { confirming > 0 }, set: { if !$0 { confirming = 0 } })) {
                Button(t(Words.download)) { if let episodes { start(episodes) } }
                Button(t(Words.cancel), role: .cancel) {}
            }
            .alert(t("Stop these downloads?"), isPresented: $stopping) {
                Button(t("Stop downloads"), role: .destructive) {
                    if let episodes { store.delete(episodes: episodes, unfinishedOnly: true) }
                }
                Button(t(Words.cancel), role: .cancel) {}
            }
            .alert(t("Remove these downloads?"), isPresented: $removing) {
                Button(t("Remove downloads"), role: .destructive) {
                    if let episodes { store.delete(episodes: episodes, unfinishedOnly: false) }
                }
                Button(t(Words.cancel), role: .cancel) {}
            }
            .alert(t("Couldn’t download this"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
                Button(t("OK"), role: .cancel) {}
            } message: {
                Text(error ?? "")
            }
    }

    private func start(_ episodes: [ItemSummary]) {
        Task {
            do { try await store.download(all: episodes) } catch { self.error = ConnectionError(error).message }
        }
    }
}

/// Download on a show's page: every episode of every season. It finds the episodes when pressed, or straight away
/// when some of the show is already here, so it can show how far it's got.
struct ShowDownloadButton: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var store
    let show: ItemSummary
    let seasons: [ItemSummary]
    @State private var episodes: [ItemSummary]?
    @State private var loading = false
    @State private var pressed = false
    @State private var loadError: String?

    var body: some View {
        let status = episodes.map { GroupDownloadStatus(store.states(of: $0)) }
        Button {
            if episodes != nil { pressed = true } else { Task { await load(); pressed = episodes != nil } }
        } label: {
            Label {
                Text(label(status))
            } icon: {
                if loading { ProgressView().frame(width: 22, height: 22) } else { DownloadStateIcon(state: status?.iconState) }
            }
        }
        .disabled(loading)
        .accessibilityLabel(status.map(accessibility) ?? t("Download show"))
        .modifier(GroupDownloadActions(episodes: episodes, trigger: $pressed, confirmsFor: show.title))
        .alert(t("Couldn’t download this"), isPresented: Binding(get: { loadError != nil }, set: { if !$0 { loadError = nil } })) {
            Button(t("OK"), role: .cancel) {}
        } message: {
            Text(loadError ?? "")
        }
        .task(id: show.id) {
            if store.hasDownloads(ofShow: show.id) { await load() }
        }
    }

    private func load() async {
        guard let client = model.client, !loading else { return }
        loading = true
        defer { loading = false }
        do {
            var all: [ItemSummary] = []
            for season in seasons {
                all += try await client.item(season.id).children.filter { $0.kind == .episode }
            }
            episodes = all
        } catch {
            loadError = ConnectionError(error).message
        }
    }

    private func label(_ status: GroupDownloadStatus?) -> String {
        switch status {
        case .downloading(_, let done, let total): t("{done} of {total}", ["done": done, "total": total])
        case .downloaded: t("Downloaded")
        case .failed: t("Couldn’t download")
        // "Show", so it isn't taken for downloading the episode Play is about to play.
        case .notStarted, nil: t("Download show")
        }
    }

    private func accessibility(_ status: GroupDownloadStatus) -> String {
        switch status {
        case .downloading(let fraction, _, _): t("Downloading, {percent}%", ["percent": Int(fraction * 100)])
        case .downloaded: t("Downloaded")
        case .failed: t("Couldn’t download")
        case .notStarted: t("Download show")
        }
    }
}

/// "Download Season 2" beside the season pills: every episode in the season, straight away.
struct SeasonDownloadLink: View {
    @Environment(DownloadStore.self) private var store
    let season: ItemDetail
    @State private var pressed = false

    var body: some View {
        let episodes = season.children.filter { $0.kind == .episode }
        let status = GroupDownloadStatus(store.states(of: episodes))
        Button { pressed = true } label: {
            HStack(spacing: 6) {
                DownloadStateIcon(state: status.iconState, size: 18)
                Text(label(status))
            }
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(Color.screen)
            .monospacedDigit()
        }
        .buttonStyle(.plain)
        .modifier(GroupDownloadActions(episodes: episodes, trigger: $pressed, confirmsFor: nil))
        .opacity(episodes.isEmpty ? 0 : 1)
        .disabled(episodes.isEmpty)
    }

    private func label(_ status: GroupDownloadStatus) -> String {
        let name = season.item.title
        switch status {
        case .notStarted: return t("Download {season}", ["season": name])
        case .downloading: return t("Downloading {season}", ["season": name])
        case .downloaded: return t("{season} downloaded", ["season": name])
        case .failed: return t("Couldn’t download")
        }
    }
}
