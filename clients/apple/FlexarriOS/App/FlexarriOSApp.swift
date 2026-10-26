import AppIntents
import BackgroundTasks
import FlexarrKit
import SwiftUI
import UserNotifications

/// Owns the downloads, so the system can hand back ones that finished while the app was closed, and wakes now and
/// then to fetch new episodes of the podcasts this phone keeps.
final class AppDelegate: NSObject, UIApplicationDelegate, UNUserNotificationCenterDelegate {
    static let podcastRefresh = "io.github.flexarr.podcasts"

    let downloads = DownloadStore()
    /// The app model, once the window's up: what a tapped notification plays or opens with.
    weak var model: AppModel?

    func application(_ application: UIApplication, didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil) -> Bool {
        UNUserNotificationCenter.current().delegate = self
        MainActor.assumeIsolated { PodcastNotifier.registerCategory() }
        BGTaskScheduler.shared.register(forTaskWithIdentifier: Self.podcastRefresh, using: .main) { [weak self] task in
            MainActor.assumeIsolated { self?.refreshPodcasts(Refresh(task: task)) }
        }
        return true
    }

    func application(_ application: UIApplication, handleEventsForBackgroundURLSession identifier: String,
                     completionHandler: @escaping () -> Void) {
        if identifier == DownloadStore.sessionIdentifier { downloads.backgroundCompletion = completionHandler }
    }

    // MARK: Notifications

    /// In front: new episodes still show, as a banner.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }

    /// A new-episode notification: Play plays it; a tap opens its show.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        let info = response.notification.request.content.userInfo
        let episodeId = info[PodcastNotifier.episodeKey] as? String
        let podcastId = info[PodcastNotifier.podcastKey] as? String
        let play = response.actionIdentifier == PodcastNotifier.playAction
        await MainActor.run {
            guard let model = self.model, let client = model.client else { return }
            Task { @MainActor in
                if play, let episodeId, let episode = try? await client.podcastEpisode(episodeId) {
                    model.playEpisode(episode.summary)
                } else if let podcastId, let podcast = try? await client.podcast(podcastId) {
                    model.pendingRoute = .item(podcast.summary)
                }
            }
        }
    }

    /// Asks to be woken in an hour or so (the system decides when, by how the phone is used).
    static func schedulePodcastRefresh() {
        let request = BGAppRefreshTaskRequest(identifier: podcastRefresh)
        request.earliestBeginDate = Date().addingTimeInterval(60 * 60)
        try? BGTaskScheduler.shared.submit(request)
    }

    /// The system's task, carried to the main actor (it's only touched there).
    private struct Refresh: @unchecked Sendable {
        let task: BGTask
    }

    @MainActor
    private func refreshPodcasts(_ refresh: Refresh) {
        Self.schedulePodcastRefresh()
        let work = Task { @MainActor in
            // Woken with no screen yet: sign in from the saved credentials, as the app would.
            let client = model?.client ?? CredentialStore().load()?.client()
            if !downloads.isAttached, let client {
                downloads.attach(client: client, profileId: nil)
            }
            await downloads.catchUp()
            if let client { await PodcastNotifier.check(client: client, profileId: downloads.profileId) }
            refresh.task.setTaskCompleted(success: !Task.isCancelled)
        }
        refresh.task.expirationHandler = { work.cancel() }
    }
}

@main
struct FlexarriOSApp: App {
    @UIApplicationDelegateAdaptor private var delegate: AppDelegate
    @State private var model: AppModel

    init() {
        ThisDevice.identify()
        // Artwork is fetched constantly while browsing; give it a generous disk cache.
        URLCache.shared = URLCache(memoryCapacity: 48 * 1024 * 1024, diskCapacity: 384 * 1024 * 1024)
        let model = AppModel()
        _model = State(initialValue: model)
        // Siri and Shortcuts intents share the sign-in and the player (see Shared/Intents), and so does the car.
        AppDependencyManager.shared.add(dependency: model)
        CarPlaySceneDelegate.model = model
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model)
                .environment(delegate.downloads)
                .preferredColorScheme(.dark)
                .tint(.silver)
                .onOpenURL { model.open(url: $0) }
                .onAppear { delegate.model = model }
        }
    }
}

struct RootView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        ZStack {
            Color.room.ignoresSafeArea()
            switch model.phase {
            case .signedOut:
                ConnectView()
                    .transition(.opacity)
            case .signedIn(let credentials):
                if !model.profilesLoaded {
                    ProgressView().controlSize(.large)
                } else if model.needsProfileChoice {
                    ProfilePickerView()
                        .transition(.opacity)
                } else {
                    // A new profile rebuilds every tab, so nothing from the last one lingers.
                    MainTabView()
                        .id("\(credentials.address)#\(credentials.profileToken ?? "")")
                        .transition(.opacity)
                }
            }
        }
        .animation(.easeInOut(duration: 0.3), value: model.phase)
        .animation(.easeInOut(duration: 0.3), value: model.needsProfileChoice)
        .task(id: model.credentials?.address) { await model.loadProfiles() }
        // Downloads follow the server and profile signed in; signing out deletes them.
        .task(id: "\(model.credentials?.address.absoluteString ?? "")#\(model.currentProfile?.id ?? "")") {
            model.player.offlineLibrary = downloads
            model.onPodcastsChange = { [weak model] in if let model { WidgetBridge.publish(model) } }
            model.music.onChange = { [weak model] in if let model { WidgetBridge.publish(model) } }
            model.music.offlineLibrary = downloads
            downloads.attach(client: model.client, profileId: model.currentProfile?.id)
            if let client = model.client { await PodcastNotifier.check(client: client, profileId: model.currentProfile?.id) }
        }
        .onChange(of: model.phase) { old, new in
            if case .signedIn = old, new == .signedOut { downloads.deleteAll() }
        }
        // Back in front: new podcast episodes, and anything else that's due. Leaving: a wake-up for later.
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, model.credentials != nil {
                Task { await downloads.catchUp() }
                Task { await model.loadUpNext() }
                if let client = model.client { Task { await PodcastNotifier.check(client: client, profileId: model.currentProfile?.id) } }
            }
            if phase == .background, model.credentials != nil {
                AppDelegate.schedulePodcastRefresh()
                WidgetBridge.publish(model)
            }
        }
        // While in front and signed in, another device (or Siri on one) can play here.
        .task(id: "\(scenePhase == .active)#\(model.credentials?.address.absoluteString ?? "")#\(model.credentials?.profileToken ?? "")#\(model.profilesLoaded)#\(model.needsProfileChoice)") {
            model.listenForCommands(scenePhase == .active)
        }
        .overlay {
            if model.player.isStarting {
                ZStack {
                    Color.black.opacity(0.55).ignoresSafeArea()
                    ProgressView().controlSize(.large).tint(.screen)
                }
                .transition(.opacity)
            }
        }
        .alert(model.player.problem == .takenOver ? "Stopped for another screen" : "Couldn't play this", isPresented: Binding(
            get: { model.player.errorMessage != nil },
            set: { if !$0 { model.player.errorMessage = nil } }
        )) {
            switch model.player.problem {
            case .takenOver:
                Button("Watch here instead") { model.player.retry() }
                Button("Go back", role: .cancel) {}
            case .busy:
                Button("Try again") { model.player.retry() }
                Button("Go back", role: .cancel) {}
            case .other:
                Button("OK", role: .cancel) {}
            }
        } message: {
            if model.player.problem == .takenOver {
                Text("\(model.player.errorMessage ?? "")\n\nThe other screen will stop if you watch here.")
            } else {
                Text(model.player.errorMessage ?? "")
            }
        }
    }
}
