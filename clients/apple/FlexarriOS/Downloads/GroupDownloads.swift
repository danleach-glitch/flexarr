import FlexarrKit
import SwiftUI

/// Where a group of episodes (a season, a whole show) stands on this device, from their downloads.
enum GroupDownloadStatus: Equatable {
    /// Some (or all) aren't here or on their way, and nothing is coming down.
    case notStarted(missing: Int)
    /// Some are on their way: how far the group has got on average, how many are here, and how many failed meanwhile
    /// (while any is still coming, the group shows its progress and can be stopped, whatever failed).
    case downloading(fraction: Double, done: Int, total: Int, failed: Int)
    /// Every one is here.
    case downloaded
    /// Nothing is on its way and some failed: the first one's reason, and how many.
    case failed(String, count: Int)

    init(_ states: [LocalDownload.State?]) {
        let failures = states.compactMap { state -> String? in if case .failed(let reason) = state { reason } else { nil } }
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
            self = .downloading(fraction: fractions.reduce(0, +) / Double(max(1, fractions.count)), done: done, total: states.count,
                                failed: failures.count)
        } else if let failure = failures.first {
            self = .failed(failure, count: failures.count)
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
        case .downloading(let fraction, _, _, _): .downloading(fraction)
        case .downloaded: .downloaded
        case .failed(let reason, _): .failed(reason)
        }
    }
}

/// A Download show or season stopped because the server didn't answer several episodes in a row.
private struct BatchUnanswered: LocalizedError {
    var errorDescription: String? { t("Couldn’t reach the server.") }
}

extension DownloadStore {
    /// Episodes in a row the server doesn't answer at all before a batch gives up on the rest.
    static let unansweredLimit = 3

    /// Each episode's download state here (nil: not downloaded), in order.
    func states(of episodes: [ItemSummary]) -> [LocalDownload.State?] {
        episodes.map { entry(for: $0.id)?.state }
    }

    /// Downloads every one of these not already here or on its way (a failed one starts again), one after another,
    /// so the server queues them in order. Carries on past a failure and throws the first one at the end, but stops
    /// once the server hasn't answered `unansweredLimit` in a row.
    /// Stopping them (`delete(episodes:)`) ends the batch too, so the rest aren't asked for after the stop. They're all
    /// asked for as the profile that started it: once someone else is watching, the batch ends.
    func download(all episodes: [ItemSummary]) async throws {
        guard let owner else { throw FlexarrClientError.invalidResponse }
        let ids = Set(episodes.map(\.id))
        stoppedEpisodeIds.subtract(ids)
        var firstError: Error?
        var unanswered = 0
        for episode in episodes {
            guard stoppedEpisodeIds.isDisjoint(with: ids), isStill(owner) else { return }
            if let existing = entry(for: episode.id) {
                if case .failed = existing.state { delete(existing.id) } else { continue }
            }
            do {
                try await download(itemId: episode.id, as: owner)
                unanswered = 0
            } catch {
                firstError = firstError ?? error
                // The server not answering at all (rather than turning one episode down) ends the batch after a few in
                // a row: each waits out its own timeout, and a long show would otherwise go on for hours.
                let noAnswer = FlexarrClient.couldNotConnect(error) || (error as? FlexarrClientError) == .unreachable
                unanswered = noAnswer ? unanswered + 1 : 0
                if unanswered >= Self.unansweredLimit { throw BatchUnanswered() }
            }
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

    /// About how much these episodes would take here at the size downloads are made at (nil when the server gives no
    /// estimate). The server estimates one title at a time, so it's asked about one of them and that's scaled by
    /// their lengths: a question per episode would be a hundred for a long show.
    func estimatedBytes(of episodes: [ItemSummary]) async -> Int64? {
        guard let client = owner?.client, !episodes.isEmpty else { return nil }
        let sample = episodes.first { ($0.durationSeconds ?? 0) > 0 } ?? episodes[0]
        guard let options = try? await client.downloadOptions(sample.id) else { return nil }
        // As `download(itemId:)` picks: an original the phone can't play comes at High.
        var choice = options.first { $0.quality == quality }
        if quality == .original, choice?.available == false { choice = options.first { $0.quality == .high } }
        guard let bytes = choice?.estimatedBytes, bytes > 0 else { return nil }
        guard let length = sample.durationSeconds, length > 0 else { return bytes * Int64(episodes.count) }
        let perSecond = Double(bytes) / length
        return Int64(episodes.reduce(0) { $0 + ($1.durationSeconds ?? length) * perSecond })
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
    /// About how much it'll take (`size`), and the space free here, when known.
    case confirm(count: Int, show: String, size: Int64?, free: Int64?)
    /// Some are still on their way (and `failed` couldn't download meanwhile).
    case stop(failed: Int)
    case remove
    /// Nothing's on its way and some couldn't download: the first one's reason, and how many.
    case retry(String, count: Int)
    case failed(String)

    var title: String {
        switch self {
        case .confirm(let count, let show, _, _): t("Download {count, plural, one {# episode} other {# episodes}} of {show}?", ["count": count, "show": show])
        case .stop: t("Stop these downloads?")
        case .remove: t("Remove these downloads?")
        case .retry(_, let count): t("Couldn’t download {count, plural, one {# episode} other {# episodes}}", ["count": count])
        case .failed: t("Couldn’t download this")
        }
    }
}

/// What pressing a group's download control does, with its questions in one alert (several alerts on one view
/// fought, so Stop took more than one press): download what's missing (a whole show asks first, with the count),
/// stop what's on its way (or try what failed meanwhile again), remove what's here, or try the failures again.
@MainActor
private enum GroupDownloadPress {
    /// `name`: what it's of (the show's, or the season's), for the question. A whole show always asks first, with the
    /// count and about how much space it'll take; a season asks only when that's more than is free here.
    static func press(_ store: DownloadStore, group: String, episodes: [ItemSummary], name: String, alwaysAsks: Bool,
                      ask: (GroupQuestion) -> Void, onError: @escaping (String) -> Void) async {
        switch store.status(ofGroup: group, episodes: episodes) {
        case .notStarted(let missing):
            // What would be asked for: not here, nor on its way.
            let wanted = episodes.filter { episode in
                guard let state = store.entry(for: episode.id)?.state else { return true }
                if case .failed = state { return true }
                return false
            }
            let size = await store.estimatedBytes(of: wanted)
            let free = DownloadStore.freeBytes
            let tooBig = if let size, let free { size > free } else { false }
            if alwaysAsks || tooBig {
                ask(.confirm(count: missing, show: name, size: size, free: free))
            } else {
                start(store, group: group, episodes: episodes, onError: onError)
            }
        case .failed(let reason, let count): ask(.retry(reason, count: count))
        case .downloading(_, _, _, let failed): ask(.stop(failed: failed))
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
            case .stop(let failed):
                // After the alert has gone, so it closes on the first press.
                Button(t("Stop downloads"), role: .destructive) { later { store.delete(episodes: episodes, unfinishedOnly: true) } }
                if failed > 0 {
                    // Only the ones that failed: the rest are still coming.
                    Button(t("Try again")) { tryAgain(failedOnly: true) }
                }
                Button(t(Words.cancel), role: .cancel) {}
            case .remove:
                Button(t("Remove downloads"), role: .destructive) { later { store.delete(episodes: episodes, unfinishedOnly: false) } }
                Button(t(Words.cancel), role: .cancel) {}
            case .retry:
                Button(t("Try again")) { tryAgain(failedOnly: false) }
                Button(t(Words.cancel), role: .cancel) {}
            case .failed:
                Button(t("OK"), role: .cancel) {}
            }
        } message: { asked in
            switch asked {
            case .confirm(_, _, let size?, let free):
                if let free, size > free {
                    Text(t("About {size}: more than the {free} free on this {device}.",
                           ["size": formatBytes(size), "free": formatBytes(free), "device": ThisDevice.name]))
                } else {
                    Text(t("About {size}.", ["size": formatBytes(size)]))
                }
            case .failed(let message), .retry(let message, _): Text(message)
            case .stop(let failed) where failed > 0:
                Text(t("{count, plural, one {# episode} other {# episodes}} couldn’t download.", ["count": failed]))
            default: EmptyView()
            }
        }
    }

    /// The ones that failed start again (and, unless `failedOnly`, any others missing).
    private func tryAgain(failedOnly: Bool) {
        let again = failedOnly ? episodes.filter { episode in
            if case .failed = store.entry(for: episode.id)?.state { true } else { false }
        } : episodes
        GroupDownloadPress.start(store, group: group, episodes: again) { message in
            Task { @MainActor in question = .failed(message) }
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
    /// The show's seasons, once its page has loaded (nil until then: the button waits, rather than finding no
    /// episodes in a page that's only a preview).
    let seasons: [ItemSummary]?
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
                await GroupDownloadPress.press(store, group: show.id, episodes: episodes, name: show.title, alwaysAsks: true,
                                               ask: { question = $0 }, onError: { question = .failed($0) })
            }
        } label: {
            Label {
                Text(label(status))
            } icon: {
                if loading || pressing { ProgressView().frame(width: 22, height: 22) } else { DownloadStateIcon(state: status?.iconState) }
            }
        }
        .disabled(seasons == nil || loading || pressing)
        .accessibilityLabel(status.map(accessibility) ?? t("Download show"))
        .modifier(GroupDownloadAlert(question: $question, group: show.id, episodes: episodes ?? []))
        // Again whenever the seasons change (the page loaded, or reloaded with more): what was found is for those.
        .task(id: seasons?.map(\.id)) {
            episodes = nil
            if store.hasDownloads(ofShow: show.id) { await load() }
        }
    }

    private func load() async {
        guard let client = model.client, let seasons, !seasons.isEmpty, !loading else { return }
        loading = true
        defer { loading = false }
        do {
            var all: [ItemSummary] = []
            for season in seasons {
                all += try await client.item(season.id).children.filter { $0.kind == .episode }
            }
            // None found isn't kept: the next press looks again.
            episodes = all.isEmpty ? nil : all
        } catch {
            question = .failed(ConnectionError(error).message)
        }
    }

    private func label(_ status: GroupDownloadStatus?) -> String {
        switch status {
        case .downloading(_, let done, let total, let failed) where failed > 0:
            t("{done} of {total}, {failed} failed", ["done": done, "total": total, "failed": failed])
        case .downloading(_, let done, let total, _): t("{done} of {total}", ["done": done, "total": total])
        case .downloaded: t("Downloaded")
        case .failed: t("Couldn’t download")
        // "Show", so it isn't taken for downloading the episode Play is about to play.
        case .notStarted, nil: t("Download show")
        }
    }

    private func accessibility(_ status: GroupDownloadStatus) -> String {
        switch status {
        case .downloading(let fraction, _, _, _): t("Downloading, {percent}%", ["percent": Int(fraction * 100)])
        case .downloaded: t("Downloaded")
        case .failed: t("Couldn’t download")
        case .notStarted: t("Download show")
        }
    }
}

/// "Download Season 2" under the season pills: every episode in the season, straight away.
struct SeasonDownloadLink: View {
    @Environment(DownloadStore.self) private var store
    let season: ItemDetail
    @State private var question: GroupQuestion?
    @State private var pressing = false

    var body: some View {
        let episodes = season.children.filter { $0.kind == .episode }
        let status = store.status(ofGroup: season.item.id, episodes: episodes)
        Button {
            // Taken at once: the size is asked about before it starts, and a second press meanwhile would start it twice.
            guard !pressing, question == nil else { return }
            pressing = true
            Task {
                defer { pressing = false }
                await GroupDownloadPress.press(store, group: season.item.id, episodes: episodes, name: season.item.title,
                                               alwaysAsks: false, ask: { question = $0 }, onError: { question = .failed($0) })
            }
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
        .disabled(episodes.isEmpty || pressing)
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
