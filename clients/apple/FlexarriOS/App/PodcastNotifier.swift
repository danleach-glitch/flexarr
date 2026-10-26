import FlexarrKit
import Foundation
import os
import UserNotifications

private let log = Logger(subsystem: Bundle.main.bundleIdentifier ?? "io.github.flexarr", category: "podcasts")

/// Tells this phone about new episodes of the podcasts its profile follows, with notifications of its own (no push
/// server): when the app wakes in the background, and when it comes to the front, the newest unplayed episodes are
/// compared with the newest one already told of. One notification per show, grouped by show; several at once from a
/// show are summed up ("3 new episodes from …"). Play on a notification plays it; tapping opens the show.
@MainActor
enum PodcastNotifier {
    nonisolated static let category = "podcast.new"
    nonisolated static let playAction = "podcast.play"
    nonisolated static let episodeKey = "episodeId"
    nonisolated static let podcastKey = "podcastId"

    private static let enabledKey = "podcasts.notify"
    private static var isChecking = false
    private static func seenKey(_ profileId: String) -> String { "podcasts.notified.\(profileId)" }

    /// The Settings switch. On by default once notifications are allowed.
    static var isEnabled: Bool {
        get { UserDefaults.standard.object(forKey: enabledKey) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: enabledKey) }
    }

    static func registerCategory() {
        let play = UNNotificationAction(identifier: playAction, title: "Play", options: [.foreground])
        let category = UNNotificationCategory(identifier: category, actions: [play], intentIdentifiers: [])
        UNUserNotificationCenter.current().setNotificationCategories([category])
    }

    /// Asks once, at a moment it makes sense (the first show followed), never at launch. True when allowed.
    @discardableResult
    static func askIfNeeded() async -> Bool {
        let center = UNUserNotificationCenter.current()
        switch await center.notificationSettings().authorizationStatus {
        case .authorized, .provisional, .ephemeral: return true
        case .denied: return false
        case .notDetermined:
            let granted = (try? await center.requestAuthorization(options: [.alert, .sound, .badge])) ?? false
            if granted { isEnabled = true }
            return granted
        @unknown default: return false
        }
    }

    static func isAllowed() async -> Bool {
        switch await UNUserNotificationCenter.current().notificationSettings().authorizationStatus {
        case .authorized, .provisional, .ephemeral: true
        default: false
        }
    }

    /// Posts what's new since last time. The first check only notes where things stand, so following a show with
    /// a long back catalogue doesn't bring a flood.
    static func check(client: FlexarrClient, profileId: String?) async {
        // Launch and coming to the front can ask at once: one at a time, so nothing is told twice.
        guard !isChecking else { return }
        isChecking = true
        defer { isChecking = false }
        let allowed = await isAllowed()
        guard let profileId, isEnabled, allowed else {
            log.info("New-episode check skipped (profile \(profileId ?? "none", privacy: .public), on \(isEnabled), allowed \(allowed))")
            return
        }
        guard let fresh = try? await client.newPodcastEpisodes(unplayed: true, take: 30) else { return }
        let key = seenKey(profileId)
        let newest = fresh.compactMap(\.publishedAt).max()
        guard let seen = UserDefaults.standard.object(forKey: key) as? Date else {
            UserDefaults.standard.set(newest ?? Date(), forKey: key)
            return
        }
        let arrived = fresh.filter { ($0.publishedAt ?? .distantPast) > seen }
        log.info("New-episode check: \(arrived.count) since \(seen, privacy: .public)")
        guard !arrived.isEmpty else { return }
        UserDefaults.standard.set(max(newest ?? seen, seen), forKey: key)

        for (podcastId, episodes) in Dictionary(grouping: arrived, by: \.podcastId) {
            let ordered = episodes.sorted { ($0.publishedAt ?? .distantPast) > ($1.publishedAt ?? .distantPast) }
            guard let first = ordered.first else { continue }
            let content = UNMutableNotificationContent()
            if ordered.count == 1 {
                content.title = "New: \(first.podcastTitle)"
                content.body = first.title
            } else {
                content.title = "\(ordered.count) new episodes from \(first.podcastTitle)"
                content.body = ordered.prefix(3).map(\.title).joined(separator: "\n")
            }
            content.sound = .default
            content.threadIdentifier = podcastId
            content.categoryIdentifier = category
            content.userInfo = [episodeKey: first.id, podcastKey: podcastId]
            let request = UNNotificationRequest(identifier: "podcast.\(first.id)", content: content, trigger: nil)
            do {
                try await UNUserNotificationCenter.current().add(request)
            } catch {
                log.error("Couldn't post a new-episode notification: \(error.localizedDescription, privacy: .public)")
            }
        }
    }
}
