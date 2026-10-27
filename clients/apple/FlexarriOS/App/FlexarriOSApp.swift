import AppIntents
import BackgroundTasks
import FlexarrKit
import StoreKit
import SwiftUI
import UserNotifications

/// Owns the downloads, so the system can hand back ones that finished while the app was closed, and wakes now and
/// then to fetch new episodes of the podcasts this phone keeps. Hears Apple's device token and what's done with a
/// notification.
final class AppDelegate: NSObject, UIApplicationDelegate, UNUserNotificationCenterDelegate {
    static let podcastRefresh = "io.github.flexarr.podcasts"

    let downloads = DownloadStore()
    let notifications = PushNotifications()
    /// The app model, once the window's up: what a tapped notification plays or opens with.
    weak var model: AppModel? {
        didSet {
            // Signing out stops this phone's notifications on the server while its sign-in still works.
            model?.onSignOut = { [notifications] client in await notifications.signedOut(client) }
        }
    }

    func application(_ application: UIApplication, didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil) -> Bool {
        UNUserNotificationCenter.current().delegate = self
        MainActor.assumeIsolated { PushNotifications.registerCategories() }
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

    func application(_ application: UIApplication, didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data) {
        notifications.received(token: deviceToken)
    }

    func application(_ application: UIApplication, didFailToRegisterForRemoteNotificationsWithError error: any Error) {
        notifications.failedToRegister(error)
    }

    /// In front: notifications still show, as a banner.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }

    /// A button on a notification, or a tap on it (which opens what it's about: the title, or the podcast).
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        let content = response.notification.request.content
        var info: [String: String] = [:]
        for case let (key as String, value as String) in content.userInfo { info[key] = value }
        await respond(to: response.actionIdentifier, category: content.categoryIdentifier, info: info)
    }

    @MainActor
    private func respond(to action: String, category: String, info: [String: String]) async {
        switch action {
        case UNNotificationDismissActionIdentifier:
            return
        case PushNotifications.downloadAction:
            await download(info["play"] ?? info["episode"], podcast: category == PushNotifications.podcastCategory)
        case PushNotifications.playOnTvAction:
            await playOnTv(info["play"] ?? info["item"])
        default:
            guard let model = await appModel() else { return }
            if action == PushNotifications.playAction, let id = info["play"] ?? info["episode"],
               let url = URL(string: "flexarr://play/\(id)") {
                model.open(url: url)
            } else if let podcastId = info["podcast"] {
                if let podcast = try? await model.client?.podcast(podcastId) { model.pendingRoute = .item(podcast.summary) }
            } else if let id = info["item"], let url = URL(string: "flexarr://item/\(id)") {
                model.open(url: url)
            }
        }
    }

    /// The app model, waiting a moment for it when a notification opened the app from closed (the window isn't up yet).
    @MainActor
    private func appModel() async -> AppModel? {
        for _ in 0..<50 where model == nil { try? await Task.sleep(for: .milliseconds(100)) }
        return model
    }

    /// The sign-in, from the app or (woken in the background with no window) from what's saved.
    @MainActor
    private var client: FlexarrClient? { model?.client ?? CredentialStore().load()?.client() }

    /// Download, without opening the app: the server gets it ready and the phone fetches it in the background.
    @MainActor
    private func download(_ itemId: String?, podcast: Bool) async {
        guard let itemId, let client else { return }
        if !downloads.isAttached { downloads.attach(client: client, profileId: nil) }
        do {
            // Podcast episodes come as they are, as the episode list's download button has them.
            if podcast {
                try await downloads.download(itemId: itemId, quality: .original)
            } else {
                try await downloads.download(itemId: itemId)
            }
        } catch {
            await tell("Couldn't download this", ConnectionError(error).message)
        }
    }

    /// Play on Apple TV: on the one Siri plays on if Flexarr is open there, otherwise the one used most recently of
    /// those with Flexarr open.
    @MainActor
    private func playOnTv(_ itemId: String?) async {
        guard let itemId, let client else { return }
        let open = ((try? await client.devices()) ?? []).filter { $0.online && !$0.isThisOne }
        guard let device = open.first(where: { $0.id == AppModel.siriDevice }) ?? open.max(by: { $0.lastUsedAt < $1.lastUsedAt }) else {
            await tell("No Apple TV to play on", "Open Flexarr on your Apple TV, then try again.")
            return
        }
        let delivered = (try? await client.play(on: device.id, itemId: itemId))?.delivered ?? false
        if !delivered { await tell("Couldn't play on \(device.name)", "Make sure Flexarr is open on it, then try again.") }
    }

    /// A notification of the phone's own, for a button that couldn't do its job with the app closed.
    private func tell(_ title: String, _ body: String) async {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        try? await UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
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
                .environment(delegate.notifications)
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
    @Environment(PushNotifications.self) private var notifications
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.requestReview) private var requestReview
    @State private var reviews = ReviewPrompt()

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
            model.player.onWatchedToEnd = { [weak reviews] closing in reviews?.watchedToEnd(closing: closing) }
            model.onPodcastsChange = { [weak model] in if let model { WidgetBridge.publish(model) } }
            model.music.onChange = { [weak model] in if let model { WidgetBridge.publish(model) } }
            model.music.offlineLibrary = downloads
            downloads.attach(client: model.client, profileId: model.currentProfile?.id)
            if model.client != nil { notifications.refresh() }
        }
        .onChange(of: model.phase) { old, new in
            if case .signedIn = old, new == .signedOut {
                downloads.deleteAll()
                notifications.forget()
            }
        }
        // Back in front: anything that's due, and Apple's device token again in case it changed. Leaving: a wake-up
        // for later.
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, model.credentials != nil {
                Task { await downloads.catchUp() }
                Task { await model.loadUpNext() }
                notifications.refresh()
                Task { await model.refreshErrorReports() }
            }
            if phase == .background, model.credentials != nil {
                AppDelegate.schedulePodcastRefresh()
                WidgetBridge.publish(model)
            }
        }
        // Apple's rating prompt, once the player has finished closing and nothing else is being said (or when the app
        // is next in front, if it went away meanwhile).
        .task(id: "\(reviews.isDue)#\(scenePhase == .active)") {
            guard reviews.isDue, scenePhase == .active else { return }
            try? await Task.sleep(for: .seconds(1.5))
            guard !Task.isCancelled, model.player.errorMessage == nil, !model.player.isStarting else { return }
            requestReview()
            reviews.asked()
        }
        // While in front and signed in, another device (or Siri on one) can play here.
        .task(id: "\(scenePhase == .active)#\(model.credentials?.address.absoluteString ?? "")#\(model.credentials?.profileToken ?? "")#\(model.profilesLoaded)#\(model.needsProfileChoice)") {
            model.listenForCommands(scenePhase == .active)
        }
        .overlay {
            if model.player.isStarting || model.liveTv.isStarting {
                ZStack {
                    Color.black.opacity(0.55).ignoresSafeArea()
                    ProgressView().controlSize(.large).tint(.screen)
                }
                .transition(.opacity)
            }
        }
        .alert("Couldn't play this channel", isPresented: Binding(
            get: { model.liveTv.errorMessage != nil },
            set: { if !$0 { model.liveTv.errorMessage = nil } }
        )) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(model.liveTv.errorMessage ?? "")
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
