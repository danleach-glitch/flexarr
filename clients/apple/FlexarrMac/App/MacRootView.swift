import FlexarrKit
import StoreKit
import SwiftUI

/// What the window shows: the welcome or connect screen, Who's watching?, or the library; and the player over all
/// of it while something plays. The iPhone's RootView, with what's the phone's own (widgets, CarPlay) left out.
struct MacRootView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @Environment(PushNotifications.self) private var notifications
    @Environment(PairingPrompts.self) private var pairing
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.requestReview) private var requestReview
    @State private var reviews = ReviewPrompt()
    @State private var player = PlayerHost.shared
    /// The welcome cards have been seen or skipped. `-FlexarrWelcomed NO` shows them again.
    @AppStorage("FlexarrWelcomed") private var welcomed = false
    @State private var showsDownloads = false
    /// The splash, for a moment at launch.
    @State private var splashing = true

    var body: some View {
        ZStack {
            Color.room.ignoresSafeArea()
            switch model.phase {
            case .signedOut:
                if !welcomed && model.store.lastAddress == nil {
                    WelcomeView { welcomed = true }
                        .frame(maxWidth: 560)
                        .transition(.opacity)
                } else {
                    ConnectView()
                        .frame(maxWidth: 640)
                        .transition(.opacity)
                }
            case .signedIn(let credentials):
                if model.tunnelProblem != nil {
                    TunnelProblemView(onDownloads: downloads.downloads.isEmpty ? nil : { showsDownloads = true })
                        .transition(.opacity)
                } else if !model.profilesLoaded {
                    ProgressView().controlSize(.large)
                } else if model.needsProfileChoice {
                    ProfilePickerView()
                        .transition(.opacity)
                } else {
                    // A new profile rebuilds every section, so nothing from the last one lingers.
                    MacMainView()
                        .id("\(credentials.address)#\(credentials.profileToken ?? "")")
                        .transition(.opacity)
                }
            }
            if splashing {
                SplashView()
                    .transition(.opacity)
                    .zIndex(2)
                    .task {
                        try? await Task.sleep(for: .milliseconds(900))
                        withAnimation(.easeOut(duration: 0.35)) { splashing = false }
                    }
            }
            // The window becomes the player while something plays (docs/mac-player.md).
            if let screen = player.shown {
                PlayerScreenView(screen: screen)
                    .ignoresSafeArea()
                    .transition(.opacity)
            }
        }
        // The iPad's layouts: a Mac window is wide.
        .environment(\.horizontalSizeClass, .regular)
        .frame(minWidth: 900, minHeight: 600)
        .toolbar(player.shown == nil ? .automatic : .hidden, for: .windowToolbar)
        .animation(.easeInOut(duration: 0.2), value: player.shown == nil)
        .sheet(isPresented: $showsDownloads) {
            NavigationStack { DownloadsView() }.frame(minWidth: 640, minHeight: 480)
        }
        .onChange(of: model.credentials == nil) { _, signedOut in if signedOut { showsDownloads = false } }
        .banner(Binding(get: { model.serverUpdateNotice }, set: { model.serverUpdateNotice = $0 }), systemImage: "exclamationmark.triangle.fill")
        .animation(.easeInOut(duration: 0.3), value: model.phase)
        .animation(.easeInOut(duration: 0.3), value: welcomed)
        .animation(.easeInOut(duration: 0.3), value: model.needsProfileChoice)
        .task(id: model.credentials?.address) { await model.loadProfiles() }
        // Downloads follow the server and profile signed in; signing out deletes them.
        .task(id: "\(model.credentials?.address.absoluteString ?? "")#\(model.currentProfile?.id ?? "")") {
            model.player.offlineLibrary = downloads
            model.player.onWatchedToEnd = { [weak reviews] closing in reviews?.watchedToEnd(closing: closing) }
            model.music.offlineLibrary = downloads
            downloads.isPlaying = { [weak model] id in model?.music.isPlayingFile(of: id) == true }
            model.onPodcastHeard = { [downloads] in Task { await downloads.catchUp() } }
            model.onBookHeard = { [downloads] id in downloads.bookFinished(itemId: id) }
            downloads.attach(client: model.client, profileId: model.currentProfile?.id)
            if model.client != nil { notifications.refresh() }
        }
        .task(id: "\(model.credentials?.address.absoluteString ?? "")#\(model.currentProfile?.id ?? "")#\(model.needsProfileChoice)") {
            if let credentials = model.credentials, model.currentProfile != nil, !model.needsProfileChoice {
                await notifications.resume(credentials)
            }
        }
        .onChange(of: model.phase) { old, new in
            if case .signedIn(let before) = old, new == .signedOut || model.credentials?.isGuest != before.isGuest {
                downloads.deleteAll()
                notifications.forget()
            }
        }
        .onChange(of: scenePhase, initial: true) { _, phase in
            if phase != .inactive { model.setInFront(phase == .active) }
            if phase == .active, model.credentials != nil {
                Task { await model.ensureGuestTunnel() }
                Task { await model.checkConnection() }
                model.warmConnection()
                Task { await downloads.catchUp() }
                Task { await model.loadUpNext() }
                notifications.refresh()
                Task { await model.refreshErrorReports() }
            }
        }
        .task(id: "\(reviews.isDue)#\(scenePhase == .active)") {
            guard reviews.isDue, scenePhase == .active else { return }
            try? await Task.sleep(for: .seconds(1.5))
            guard !Task.isCancelled, model.player.errorMessage == nil, !model.player.isStarting else { return }
            requestReview()
            reviews.asked()
        }
        // While signed in, another device (or Siri on one) can play here. A Mac in the background still plays.
        .task(id: "\(model.credentials?.address.absoluteString ?? "")#\(model.credentials?.profileToken ?? "")#\(model.profilesLoaded)#\(model.needsProfileChoice)") {
            model.listenForCommands(model.credentials != nil)
            if model.client == nil {
                pairing.use(nil)
            } else if model.profilesLoaded, !model.needsProfileChoice {
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
        .alert(t("Couldn't request this"), isPresented: Binding(
            get: { model.requestError != nil },
            set: { if !$0 { model.requestError = nil } }
        )) {
            Button(t("OK"), role: .cancel) {}
        } message: {
            Text(model.requestError ?? "")
        }
        .alert(t("Couldn't change your watchlist"), isPresented: Binding(
            get: { model.watchlistError != nil },
            set: { if !$0 { model.watchlistError = nil } }
        )) {
            Button(t("OK"), role: .cancel) {}
        } message: {
            Text(model.watchlistError ?? "")
        }
        .alert(t("Couldn't play this channel"), isPresented: Binding(
            get: { model.liveTv.errorMessage != nil },
            set: { if !$0 { model.liveTv.errorMessage = nil } }
        )) {
            Button(t("OK"), role: .cancel) {}
        } message: {
            Text(model.liveTv.errorMessage ?? "")
        }
        .alert(model.player.problem == .takenOver ? t("Stopped for another screen") : t("Couldn't play this"), isPresented: Binding(
            get: { model.player.errorMessage != nil },
            set: { if !$0 { model.player.errorMessage = nil } }
        )) {
            switch model.player.problem {
            case .takenOver:
                Button(t("Watch here instead")) { model.player.retry() }
                Button(t("Go back"), role: .cancel) {}
            case .busy:
                Button(t("Try again")) { model.player.retry() }
                Button(t("Go back"), role: .cancel) {}
            case .other:
                Button(t("OK"), role: .cancel) {}
            }
        } message: {
            if model.player.problem == .takenOver {
                Text((model.player.errorMessage ?? "") + "\n\n" + t("The other screen will stop if you watch here."))
            } else {
                Text(model.player.errorMessage ?? "")
            }
        }
    }
}
