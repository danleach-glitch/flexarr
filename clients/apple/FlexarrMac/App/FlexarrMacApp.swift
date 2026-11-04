import AppIntents
import AppKit
import FlexarrKit
import SwiftUI
import UserNotifications

/// Owns the downloads and notifications, and hears Apple's device token and what's done with a notification. The
/// iPhone's app delegate does the same (FlexarriOS/App/FlexarriOSApp.swift), with the phone's extras.
@MainActor
final class MacAppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    let downloads = DownloadStore()
    let notifications = PushNotifications()
    /// Devices asking to connect, for this Mac to allow when it's signed in as the main profile.
    let pairing = PairingPrompts()
    /// The app model, once the window's up: what a clicked notification plays or opens with.
    weak var model: AppModel? {
        didSet {
            // Signing out sends what was watched offline, and stops this Mac's notifications on the server, while its
            // sign-in still works.
            model?.onSignOut = { [notifications, downloads] client in
                await downloads.sendLeftoverProgress(with: client)
                await notifications.signedOut(client)
            }
            model?.onPairingEvent = { [pairing] event in pairing.received(event) }
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        UNUserNotificationCenter.current().delegate = self
        PushNotifications.registerCategories()
        NotificationCenter.default.addObserver(forName: Localization.changed, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { PushNotifications.registerCategories() }
        }
    }

    /// Closing the window ends the app, as for Apple's TV and Music apps when their only window goes (a film playing
    /// in Picture in Picture keeps it open).
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        PlayerHost.shared.screen == nil
    }

    func application(_ application: NSApplication, didRegisterForRemoteNotificationsWithDeviceToken deviceToken: Data) {
        notifications.received(token: deviceToken)
    }

    func application(_ application: NSApplication, didFailToRegisterForRemoteNotificationsWithError error: any Error) {
        notifications.failedToRegister(error)
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        for url in urls { model?.open(url: url) }
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .list, .sound]
    }

    /// A click on a notification (or one of its buttons): opens what it's about.
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                            withCompletionHandler completionHandler: @escaping () -> Void) {
        let content = response.notification.request.content
        var info: [String: String] = [:]
        for case let (key as String, value as String) in content.userInfo { info[key] = value }
        let (action, category) = (response.actionIdentifier, content.categoryIdentifier)
        nonisolated(unsafe) let handled = completionHandler
        Task { @MainActor in
            await respond(to: action, category: category, info: info)
            handled()
        }
    }

    @MainActor
    private func respond(to action: String, category: String, info: [String: String]) async {
        guard action != UNNotificationDismissActionIdentifier else { return }
        NSApp.activate()
        for _ in 0..<50 where model == nil { try? await Task.sleep(for: .milliseconds(100)) }
        guard let model else { return }
        if let code = info["pairing"] {
            pairing.use(model.client)
            await pairing.open(code: code)
        } else if category == PushNotifications.pinCategory {
            model.pendingSettings = true
        } else if action == PushNotifications.downloadAction, let id = info["play"] ?? info["episode"] {
            if !downloads.isAttached { downloads.attach(client: model.client, profileId: model.currentProfile?.id) }
            try? await downloads.download(itemId: id)
        } else if action == PushNotifications.playAction, let id = info["play"] ?? info["episode"], let url = URL(string: "flexarr://play/\(id)") {
            model.open(url: url)
        } else if let podcastId = info["podcast"] {
            if let podcast = try? await model.client?.podcast(podcastId) { model.pendingRoute = .item(podcast.summary) }
        } else if let id = info["item"], let url = URL(string: "flexarr://item/\(id)") {
            model.open(url: url)
        }
    }
}

/// The Mac app: the player (docs/mac-player.md). Flexarr Server is a different app, in clients/macos.
@main
struct FlexarrMacApp: App {
    @NSApplicationDelegateAdaptor private var delegate: MacAppDelegate
    @State private var model: AppModel
    @State private var language: LanguageState

    init() {
        Localization.shared.startWithLastShown()
        _language = State(initialValue: LanguageState())
        Symbol.checkAll()
        ThisDevice.identify()
        // Artwork is fetched constantly while browsing; give it a generous disk cache.
        URLCache.shared = URLCache(memoryCapacity: 64 * 1024 * 1024, diskCapacity: 512 * 1024 * 1024)
        let model = AppModel()
        _model = State(initialValue: model)
        // Siri and Shortcuts intents share the sign-in and the player (see Shared/Intents).
        AppDependencyManager.shared.add(dependency: model)
    }

    var body: some Scene {
        Window("Flexarr", id: "main") {
            MacRootView()
                // A new language redraws everything in it.
                .id(language.code)
                .environment(\.locale, language.locale)
                .environment(\.layoutDirection, language.isRightToLeft ? .rightToLeft : .leftToRight)
                .environment(model)
                .environment(delegate.downloads)
                .environment(delegate.notifications)
                .environment(delegate.pairing)
                .preferredColorScheme(.dark)
                .tint(.silver)
                .toggleStyle(GoSwitchStyle())
                .onOpenURL { model.open(url: $0) }
                .onContinueUserActivity(NSUserActivityTypeBrowsingWeb) { activity in
                    if let url = activity.webpageURL { model.open(url: url) }
                }
                .sheet(item: Bindable(model).pendingInvite) { JoinInviteView(invite: $0).environment(model) }
                .alert(model.inviteNotice ?? "", isPresented: Binding(
                    get: { model.inviteNotice != nil },
                    set: { if !$0 { model.inviteNotice = nil } }
                )) {
                    Button(t("OK"), role: .cancel) {}
                }
                .onAppear {
                    delegate.model = model
                    #if DEBUG
                    DebugSnapshots.currentModel = model
                    DebugSnapshots.runIfAsked()
                    #endif
                }
        }
        .defaultSize(width: 1280, height: 800)
        .windowToolbarStyle(.unified(showsTitle: false))
        .commands { FlexarrCommands(model: model) }
    }
}
