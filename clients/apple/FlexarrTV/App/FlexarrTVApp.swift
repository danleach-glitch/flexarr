import AppIntents
import FlexarrKit
import SwiftUI
import TVServices

@main
struct FlexarrTVApp: App {
    @State private var model: AppModel
    @State private var language: LanguageState

    init() {
        // The language shown last, before anything draws (tvOS has no App Group: the Top Shelf reads its own copy).
        Localization.shared.startWithLastShown()
        _language = State(initialValue: LanguageState())
        Symbol.checkAll()
        ThisDevice.identify()
        // Artwork is fetched constantly while browsing; give it a generous disk cache.
        URLCache.shared = URLCache(memoryCapacity: 64 * 1024 * 1024, diskCapacity: 512 * 1024 * 1024)
        let model = AppModel()
        _model = State(initialValue: model)
        // The guide over the live picture is the Apple TV's (it's the grid from the Live TV screen).
        model.liveTv.makeGuide = { client, channels, current, onWatch, onClose in
            LiveGuideOverlay.controller(client: client, channels: channels, currentId: current, records: !model.hasAgeLimit,
                                        onWatch: onWatch, onClose: onClose)
        }
        // Siri and Shortcuts intents share the sign-in and the player (see Shared/Intents).
        AppDependencyManager.shared.add(dependency: model)
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                // A new language redraws everything in it.
                .id(language.code)
                .environment(\.locale, language.locale)
                .environment(\.layoutDirection, language.isRightToLeft ? .rightToLeft : .leftToRight)
                .environment(model)
                .preferredColorScheme(.dark)
                .onOpenURL { model.open(url: $0) }
        }
    }
}

struct RootView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        ZStack {
            Color.room.ignoresSafeArea()
            switch model.phase {
            case .signedOut:
                ConnectView()
                    .transition(.opacity)
            case .signedIn(let credentials):
                if model.tunnelProblem != nil {
                    TunnelProblemView()
                        .transition(.opacity)
                } else if model.isOffline && !model.hasBeenOnline {
                    // Offline from the start: nothing to keep underneath.
                    OfflineView()
                        .transition(.opacity)
                } else if !model.profilesLoaded {
                    ProgressView().scaleEffect(1.4)
                } else if model.needsProfileChoice {
                    ProfilePickerView()
                        .transition(.opacity)
                } else {
                    // A new profile rebuilds every tab, so nothing from the last one lingers.
                    // Offline after the server had answered: the notice covers the screens rather than replace them, so the
                    // TV carries on where it was once the server's back.
                    MainTabView()
                        .id("\(credentials.address)#\(credentials.profileToken ?? "")")
                        .disabled(model.isOffline)
                        .overlay {
                            if model.isOffline {
                                OfflineView()
                                    .background(Color.room.ignoresSafeArea())
                                    .transition(.opacity)
                            }
                        }
                        .transition(.opacity)
                }
            }
        }
        // "Update your Flexarr server": signed in to a server older than the app supports. Goes by itself.
        .overlay(alignment: .top) {
            if let notice = model.serverUpdateNotice {
                Label(notice, systemImage: "exclamationmark.triangle.fill")
                    .font(.system(size: 28, weight: .semibold))
                    .foregroundStyle(Color.screen)
                    .padding(.horizontal, 32)
                    .padding(.vertical, 18)
                    .background(Capsule().fill(Color.seat))
                    .padding(.top, 40)
                    .transition(.move(edge: .top).combined(with: .opacity))
                    .task(id: notice) {
                        try? await Task.sleep(for: .seconds(6))
                        if model.serverUpdateNotice == notice { model.serverUpdateNotice = nil }
                    }
            }
        }
        .animation(.easeInOut(duration: 0.3), value: model.serverUpdateNotice)
        .animation(.easeInOut(duration: 0.35), value: model.phase)
        .animation(.easeInOut(duration: 0.35), value: model.needsProfileChoice)
        .animation(.easeInOut(duration: 0.35), value: model.isOffline)
        .task(id: model.credentials?.address) { await model.loadProfiles() }
        // Top Shelf follows whoever's signed in here; after watching, its Continue Watching is refreshed.
        .task(id: model.credentials) {
            // A family guest's Top Shelf stays empty: the extension runs apart from the app, so it can't reach the tunnel.
            SharedSignIn.save(model.credentials.flatMap { $0.isGuest ? nil : SharedSignIn.Value(address: $0.address, apiKey: $0.apiKey, profileToken: $0.profileToken) })
            TVTopShelfContentProvider.topShelfContentDidChange()
        }
        .onChange(of: model.refreshToken) { TVTopShelfContentProvider.topShelfContentDidChange() }
        // While in front and signed in, an iPhone (or Siri on it), the web app or an assistant can play here.
        .task(id: "\(scenePhase == .active)#\(model.credentials?.address.absoluteString ?? "")#\(model.credentials?.profileToken ?? "")#\(model.profilesLoaded)#\(model.needsProfileChoice)") {
            model.listenForCommands(scenePhase == .active)
        }
        .onChange(of: scenePhase) { _, phase in
            // Offline mode only tries the server again while the app is in front.
            if phase != .inactive { model.setInFront(phase == .active) }
            if phase == .active {
                // A guest's tunnel may have stopped while the app was away: back up before anything asks.
                Task { await model.ensureGuestTunnel() }
                // Offline ends (or starts) as soon as it's known whether the server answers now.
                Task { await model.checkConnection() }
                model.warmConnection()
                Task { await model.refreshErrorReports() }
            }
        }
        .overlay {
            if model.player.isStarting || model.liveTv.isStarting {
                ZStack {
                    Color.black.opacity(0.55).ignoresSafeArea()
                    ProgressView()
                        .scaleEffect(1.6)
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
        .alert(model.player.problem == .takenOver ? "Stopped for another screen" : "Couldn't play this", isPresented: Binding(
            get: { model.player.errorMessage != nil },
            set: { if !$0 { model.player.errorMessage = nil } }
        )) {
            // The main button comes first, so it has the focus.
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
                Text("\(model.player.errorMessage ?? "")\n\nThe other screen will stop if you watch here.")
            } else {
                Text(model.player.errorMessage ?? "")
            }
        }
    }
}
