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
    /// Stopping them (`delete(episodes:)`) ends the batch too, so the rest aren't asked for after the stop. They're all
    /// asked for as the profile that started it: once someone else is watching, the batch ends.
    func download(all episodes: [ItemSummary]) async throws {
        guard let owner else { throw FlexarrClientError.invalidResponse }
        let ids = Set(episodes.map(\.id))
        stoppedEpisodeIds.subtract(ids)
        var firstError: Error?
        for episode in episodes {
            guard stoppedEpisodeIds.isDisjoint(with: ids), isStill(owner) else { return }
            if let existing = entry(for: episode.id) {
                if case .failed = existing.state { delete(existing.id) } else { continue }
            }
            do { try await download(itemId: episode.id, as: owner) } catch { firstError = firstError ?? error }
            // Someone else is watching now (or no one: signed out): the rest aren’t theirs to ask for.
            guard isStill(owner) else { return }
            // Stopped while the server was being asked: this one arrived after the stop.
            if !stoppedEpisodeIds.isDisjoint(with: ids) {
                if let late = entry(for: episode.id), !late.isDownloaded { delete(late.id) }
                return
            }
        }
        if let firstError { throw firstError }
    }

    /// Deletes these episodes' downloads: all of them, or only those still on their way. Any batch still asking for
    /// them stops, and the show and season they're in no longer count as started.
    func delete(episodes: [ItemSummary], unfinishedOnly: Bool) {
        stoppedEpisodeIds.formUnion(episodes.map(\.id))
        for episode in episodes {
            for group in [episode.seriesId, episode.parentId].compactMap({ $0 }) { forgetStarted(group) }
            guard let download = entry(for: episode.id) else { continue }
            if unfinishedOnly && download.isDownloaded { continue }
            delete(download.id)
        }
    }

    /// Whether anything of this show is downloaded or on its way here.
    func hasDownloads(ofShow showId: String) -> Bool {
        all.contains { $0.profileId == profileId && $0.item.seriesId == showId }
    }

    private func groupKey(_ id: String) -> String { "\(profileId ?? ""):\(id)" }
    func isStarted(_ groupId: String) -> Bool { startedGroups.contains(groupKey(groupId)) }
    func markStarted(_ groupId: String) { startedGroups.insert(groupKey(groupId)) }
    func forgetStarted(_ groupId: String) { startedGroups.remove(groupKey(groupId)) }

    /// A show's or season's control: all here reads as downloaded however they came; otherwise its progress shows only
    /// once the whole group was started from it, so downloading one episode leaves the season and show as they were.
    func status(ofGroup groupId: String, episodes: [ItemSummary]) -> GroupDownloadStatus {
        let states = states(of: episodes)
        let status = GroupDownloadStatus(states)
        if status == .downloaded || isStarted(groupId) { return status }
        return .notStarted(missing: states.filter { $0 != .downloaded }.count)
    }
}

/// The one question a group's download control is asking.
private enum GroupQuestion {
    case confirm(count: Int, show: String)
    case stop
    case remove
    case failed(String)

    var title: String {
        switch self {
        case .confirm(let count, let show): t("Download {count, plural, one {# episode} other {# episodes}} of {show}?", ["count": count, "show": show])
        case .stop: t("Stop these downloads?")
        case .remove: t("Remove these downloads?")
        case .failed: t("Couldn’t download this")
        }
    }
}

/// What pressing a group's download control does, with its questions in one alert (several alerts on one view
/// fought, so Stop took more than one press): download what's missing (a whole show asks first, with the count),
/// stop what's on its way, remove what's here, or try a failure again.
@MainActor
private enum GroupDownloadPress {
    static func press(_ store: DownloadStore, group: String, episodes: [ItemSummary], confirmsFor show: String?,
                      ask: (GroupQuestion) -> Void, onError: @escaping (String) -> Void) {
        switch store.status(ofGroup: group, episodes: episodes) {
        case .notStarted(let missing):
            if let show { ask(.confirm(count: missing, show: show)) } else { start(store, group: group, episodes: episodes, onError: onError) }
        case .failed: start(store, group: group, episodes: episodes, onError: onError)
        case .downloading: ask(.stop)
        case .downloaded: ask(.remove)
        }
    }

    static func start(_ store: DownloadStore, group: String, episodes: [ItemSummary], onError: @escaping (String) -> Void) {
        store.markStarted(group)
        Task {
            do { try await store.download(all: episodes) } catch { onError(ConnectionError(error).message) }
        }
    }
}

private struct GroupDownloadAlert: ViewModifier {
    @Environment(DownloadStore.self) private var store
    @Binding var question: GroupQuestion?
    let group: String
    let episodes: [ItemSummary]

    func body(content: Content) -> some View {
        content.alert(question?.title ?? "", isPresented: Binding(get: { question != nil }, set: { if !$0 { question = nil } }),
                      presenting: question) { asked in
            switch asked {
            case .confirm:
                Button(t(Words.download)) {
                    GroupDownloadPress.start(store, group: group, episodes: episodes) { message in
                        Task { @MainActor in question = .failed(message) }
                    }
                }
                Button(t(Words.cancel), role: .cancel) {}
            case .stop:
                // After the alert has gone, so it closes on the first press.
                Button(t("Stop downloads"), role: .destructive) { later { store.delete(episodes: episodes, unfinishedOnly: true) } }
                Button(t(Words.cancel), role: .cancel) {}
            case .remove:
                Button(t("Remove downloads"), role: .destructive) { later { store.delete(episodes: episodes, unfinishedOnly: false) } }
                Button(t(Words.cancel), role: .cancel) {}
            case .failed:
                Button(t("OK"), role: .cancel) {}
            }
        } message: { asked in
            if case .failed(let message) = asked { Text(message) }
        }
    }

    private func later(_ body: @escaping @MainActor () -> Void) {
        Task { @MainActor in
            await Task.yield()
            body()
        }
    }
}

/// Download show on a show's page: every episode of every season. It finds the episodes when pressed, or straight
/// away when some of the show is already here, so it can tell when it's all here.
struct ShowDownloadButton: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var store
    let show: ItemSummary
    let seasons: [ItemSummary]
    @State private var episodes: [ItemSummary]?
    @State private var loading = false
    @State private var pressing = false
    @State private var question: GroupQuestion?

    var body: some View {
        let status = episodes.map { store.status(ofGroup: show.id, episodes: $0) }
        Button {
            // Taken at once, before the episodes load: a second press while they did asked again, and that question
            // came back after Cancel.
            guard !pressing, question == nil else { return }
            pressing = true
            Task {
                defer { pressing = false }
                if episodes == nil { await load() }
                guard let episodes, question == nil else { return }
                GroupDownloadPress.press(store, group: show.id, episodes: episodes, confirmsFor: show.title,
                                         ask: { question = $0 }, onError: { question = .failed($0) })
            }
        } label: {
            Label {
                Text(label(status))
            } icon: {
                if loading { ProgressView().frame(width: 22, height: 22) } else { DownloadStateIcon(state: status?.iconState) }
            }
        }
        .disabled(loading || pressing)
        .accessibilityLabel(status.map(accessibility) ?? t("Download show"))
        .modifier(GroupDownloadAlert(question: $question, group: show.id, episodes: episodes ?? []))
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
            question = .failed(ConnectionError(error).message)
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
    @State private var question: GroupQuestion?

    var body: some View {
        let episodes = season.children.filter { $0.kind == .episode }
        let status = store.status(ofGroup: season.item.id, episodes: episodes)
        Button {
            GroupDownloadPress.press(store, group: season.item.id, episodes: episodes, confirmsFor: nil,
                                     ask: { question = $0 }, onError: { question = .failed($0) })
        } label: {
            HStack(spacing: 6) {
                DownloadStateIcon(state: status.iconState, size: 18)
                Text(label(status))
            }
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(Color.screen)
            .monospacedDigit()
        }
        .buttonStyle(.plain)
        .modifier(GroupDownloadAlert(question: $question, group: season.item.id, episodes: episodes))
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
