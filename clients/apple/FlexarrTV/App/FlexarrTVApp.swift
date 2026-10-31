import AppIntents
import FlexarrKit
import SwiftUI
import TVServices

@main
struct FlexarrTVApp: App {
    @State private var model: AppModel

    init() {
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
                if let problem = model.tunnelProblem {
                    TunnelProblemView(problem: problem)
                        .transition(.opacity)
                } else if !model.profilesLoaded {
                    ProgressView().scaleEffect(1.4)
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
        .animation(.easeInOut(duration: 0.35), value: model.phase)
        .animation(.easeInOut(duration: 0.35), value: model.needsProfileChoice)
        .task(id: model.credentials?.address) { await model.loadProfiles() }
        // Top Shelf follows whoever's signed in here; after watching, its Continue Watching is refreshed.
        .task(id: model.credentials) {
            SharedSignIn.save(model.credentials.map { SharedSignIn.Value(address: $0.address, apiKey: $0.apiKey, profileToken: $0.profileToken) })
            TVTopShelfContentProvider.topShelfContentDidChange()
        }
        .onChange(of: model.refreshToken) { TVTopShelfContentProvider.topShelfContentDidChange() }
        // While in front and signed in, an iPhone (or Siri on it), the web app or an assistant can play here.
        .task(id: "\(scenePhase == .active)#\(model.credentials?.address.absoluteString ?? "")#\(model.credentials?.profileToken ?? "")#\(model.profilesLoaded)#\(model.needsProfileChoice)") {
            model.listenForCommands(scenePhase == .active)
        }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active {
                // A guest's tunnel may have stopped while the app was away: back up before anything asks.
                Task { await model.ensureGuestTunnel() }
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
            // The main button comes first, so it has the focus.
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
