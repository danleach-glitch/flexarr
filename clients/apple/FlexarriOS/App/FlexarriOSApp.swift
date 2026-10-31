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
    /// Devices asking to connect, for this phone to allow when it's signed in as the main profile.
    let pairing = PairingPrompts()
    /// The TVs in the house playing something, for the Playing on TV bar and the remote.
    let remote = TVRemote()
    /// The app model, once the window's up: what a tapped notification plays or opens with.
    weak var model: AppModel? {
        didSet {
            // Signing out sends what was watched offline, and stops this phone's notifications on the server, while its
            // sign-in still works.
            model?.onSignOut = { [notifications, downloads] client in
                await downloads.sendLeftoverProgress(with: client)
                await notifications.signedOut(client)
            }
            model?.onPairingEvent = { [pairing] event in pairing.received(event) }
            model?.onNowPlaying = { [remote] device, playing, online in remote.received(device, playing, online: online) }
            model?.onOpenRemote = { [remote] device in remote.open(on: device) }
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

    /// In front: notifications still show, as a banner. Except a device asking to connect, which the app asks about
    /// itself (the command stream has usually said already; the list is fetched in case it hasn't).
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        if notification.request.content.categoryIdentifier == PushNotifications.pairingCategory,
           await catchUpOnPairing() {
            return []
        }
        return [.banner, .list, .sound]
    }

    /// Fetches the devices waiting to connect; false when the window isn't up to ask about them.
    @MainActor
    private func catchUpOnPairing() async -> Bool {
        guard let model, model.client != nil, model.profilesLoaded, !model.needsProfileChoice else { return false }
        pairing.use(model.client)
        await pairing.catchUp()
        return true
    }

    /// A button on a notification, or a tap on it (which opens what it's about: the title, or the podcast). Answered on
    /// the main thread: UIKit updates the app's snapshot when it's told the tap is handled, and told from elsewhere (as
    /// the async form of this did) that crashed the app.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        let content = response.notification.request.content
        var info: [String: String] = [:]
        for case let (key as String, value as String) in content.userInfo { info[key] = value }
        let (action, category, title) = (response.actionIdentifier, content.categoryIdentifier, content.title)
        nonisolated(unsafe) let handled = completionHandler
        Task { @MainActor in
            await respond(to: action, category: category, title: title, info: info)
            handled()
        }
    }

    @MainActor
    private func respond(to action: String, category: String, title: String, info: [String: String]) async {
        switch action {
        case UNNotificationDismissActionIdentifier:
            return
        case PushNotifications.allowPairingAction:
            // Opens the app (the action's in front) on that device's prompt and allows it there, which asks for the
            // admin PIN when it's locked here.
            guard let code = info["pairing"], let model = await appModel() else { return }
            pairing.use(model.client)
            await pairing.open(code: code)
            if pairing.current?.code == code { await pairing.allow(code) }
        case PushNotifications.declinePairingAction:
            guard let code = info["pairing"] else { return }
            await turnAway(code, title: title)
        case PushNotifications.downloadAction:
            await download(info["play"] ?? info["episode"], podcast: category == PushNotifications.podcastCategory)
        case PushNotifications.playOnTvAction:
            await playOnTv(info["play"] ?? info["item"])
        default:
            guard let model = await appModel() else { return }
            // "Living room wants to connect": asks about it here (the window may not have caught up on the sign-in yet).
            if let code = info["pairing"] {
                pairing.use(model.client)
                await pairing.open(code: code)
            } else if category == PushNotifications.pinCategory {
                model.pendingSettings = true
            } else if action == PushNotifications.playAction, let id = info["play"] ?? info["episode"],
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

    /// Don't allow on a "wants to connect" notification, without opening the app (which may have been woken just for
    /// this, with no window: the sign-in is the saved one). Says how it went in a notification of the phone's own only
    /// if it didn't. (A locked main profile may still turn devices away; allowing one opens the app.)
    @MainActor
    private func turnAway(_ code: String, title: String) async {
        await PushNotifications.removePairingNotifications { $0 == code }
        // The server's title is "Living room wants to connect".
        let suffix = " wants to connect"
        let name = title.hasSuffix(suffix) ? "“\(title.dropLast(suffix.count))”" : nil
        let failed = "Couldn't turn away \(name ?? "the device")"
        guard let client else {
            await tell(failed, "This \(ThisDevice.name) isn't signed in to Flexarr.")
            return
        }
        do {
            try await client.declinePairing(code: code)
            pairing.stoppedWaiting(code)
        } catch FlexarrClientError.http(status: 404, _) {
            pairing.stoppedWaiting(code) // Gone already, which is what was wanted.
        } catch FlexarrClientError.http(status: 403, _) {
            await tell(failed, "Switch to the main profile to turn it away.")
        } catch {
            await tell(failed, ConnectionError(error).message)
        }
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
                .environment(delegate.pairing)
                .environment(delegate.remote)
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
    @Environment(PairingPrompts.self) private var pairing
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.requestReview) private var requestReview
    @State private var reviews = ReviewPrompt()
    /// The welcome cards have been seen or skipped. `-FlexarrWelcomed NO` shows them again.
    @AppStorage("FlexarrWelcomed") private var welcomed = false

    var body: some View {
        ZStack {
            Color.room.ignoresSafeArea()
            switch model.phase {
            case .signedOut:
                // Only a fresh install gets the welcome: anyone who has connected before knows about the server.
                if !welcomed && model.store.lastAddress == nil {
                    WelcomeView { welcomed = true }
                        .transition(.opacity)
                } else {
                    ConnectView()
                        .transition(.opacity)
                }
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
        .animation(.easeInOut(duration: 0.3), value: welcomed)
        .animation(.easeInOut(duration: 0.3), value: model.needsProfileChoice)
        .task(id: model.credentials?.address) { await model.loadProfiles() }
        // Downloads follow the server and profile signed in; signing out deletes them.
        .task(id: "\(model.credentials?.address.absoluteString ?? "")#\(model.currentProfile?.id ?? "")") {
            model.player.offlineLibrary = downloads
            model.player.onWatchedToEnd = { [weak reviews] closing in reviews?.watchedToEnd(closing: closing) }
            model.onPodcastsChange = { [weak model] in if let model { WidgetBridge.publish(model) } }
            model.music.onChange = { [weak model] in if let model { WidgetBridge.publish(model) } }
            model.onWatchlistChange = { [weak model] in if let model { WidgetBridge.publishWatchlist(model) } }
            model.onWatchlistForgotten = { WidgetBridge.forgetWatchlist() }
            model.music.offlineLibrary = downloads
            downloads.isPlaying = { [weak model] id in model?.music.isPlayingFile(of: id) == true }
            model.onPodcastHeard = { [downloads] in Task { await downloads.catchUp() } }
            downloads.attach(client: model.client, profileId: model.currentProfile?.id)
            if model.client != nil { notifications.refresh() }
        }
        // Notifications on again for a phone signed in again, once the profile's chosen, so it hears about the profile
        // it's on. Its own task, following "Who's watching?" too: a phone signed in again can know its profile before
        // it's asked, and answering with the same one changes nothing the task above follows.
        .task(id: "\(model.credentials?.address.absoluteString ?? "")#\(model.currentProfile?.id ?? "")#\(model.needsProfileChoice)") {
            if let credentials = model.credentials, model.currentProfile != nil, !model.needsProfileChoice {
                await notifications.resume(credentials)
            }
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
                // A Watchlist widget placed since the watchlist last changed has nothing yet: what's ready now.
                WidgetBridge.publishWatchlist(model)
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
            // Devices that asked to connect while the app was away (the stream only says as they start).
            if model.client == nil {
                pairing.use(nil)
            } else if scenePhase == .active, model.profilesLoaded, !model.needsProfileChoice {
                pairing.use(model.client)
                await pairing.catchUp()
            }
        }
        .pairingPrompts(pairing)
        .overlay {
            if model.player.isStarting || model.liveTv.isStarting {
                ZStack {
                    Color.black.opacity(0.55).ignoresSafeArea()
                    ProgressView().controlSize(.large).tint(.screen)
                }
                .transition(.opacity)
            }
        }
        .alert("Couldn't request this", isPresented: Binding(
            get: { model.requestError != nil },
            set: { if !$0 { model.requestError = nil } }
        )) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(model.requestError ?? "")
        }
        .alert("Couldn't change your watchlist", isPresented: Binding(
            get: { model.watchlistError != nil },
            set: { if !$0 { model.watchlistError = nil } }
        )) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(model.watchlistError ?? "")
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
