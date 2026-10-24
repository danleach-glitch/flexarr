import FlexarrKit
import SwiftUI

enum TabID: Hashable {
    case home
    case library(String)
    case search
    case settings
    case profiles
}

/// tvOS top tabs: Home, one per library, Search, Settings, and who's watching when there are several profiles.
struct MainTabView: View {
    @Environment(AppModel.self) private var model
    @State private var selection: TabID = .home

    var body: some View {
        TabView(selection: $selection) {
            Tab(value: TabID.home) {
                TabStack(handlesDeepLinks: true) { HomeView() }
            } label: {
                Text("Home")
            }
            ForEach(model.libraries) { library in
                Tab(value: TabID.library(library.id)) {
                    TabStack { LibraryView(library: library) }
                } label: {
                    Text(library.name)
                }
            }
            Tab(value: TabID.search, role: .search) {
                TabStack { SearchView() }
            }
            Tab(value: TabID.settings) {
                TabStack { SettingsView() }
            } label: {
                Text("Settings")
            }
            // At the far end, where the Apple TV app keeps its own profile button.
            if model.profiles.count > 1, let current = model.currentProfile {
                Tab(value: TabID.profiles) {
                    ProfilePickerView(marksCurrent: true)
                } label: {
                    Text(current.name)
                }
            }
        }
        .task { await model.loadLibraries() }
        .onChange(of: model.pendingRoute) { _, route in
            if route != nil { selection = .home }
        }
    }
}

/// A navigation stack per tab with the shared destinations.
struct TabStack<Content: View>: View {
    @Environment(AppModel.self) private var model
    @State private var router = Router()
    var handlesDeepLinks = false
    @ViewBuilder var content: () -> Content

    var body: some View {
        @Bindable var router = router
        NavigationStack(path: $router.path) {
            content()
                .navigationDestination(for: Route.self) { route in
                    destination(route)
                }
        }
        .environment(router)
        .onChange(of: model.pendingRoute) { _, _ in consumeDeepLink() }
        .onAppear(perform: consumeDeepLink)
    }

    private func consumeDeepLink() {
        guard handlesDeepLinks, let route = model.pendingRoute else { return }
        router.push(route)
        model.pendingRoute = nil
    }

    @ViewBuilder
    private func destination(_ route: Route) -> some View {
        switch route {
        case .item(let item):
            DetailView(id: item.id, preview: item)
        case .itemID(let id):
            DetailView(id: id, preview: nil)
        case .person(let id, let name):
            PersonView(id: id, name: name)
        case .library(let library, let sort):
            LibraryView(library: library, initialSort: sort)
        }
    }
}

/// How long a screen's data counts as current. tvOS keeps apps alive for days, and changes made elsewhere
/// (watching on the web, a new download) reach a screen only when it fetches again.
enum Freshness {
    static let maxAge: TimeInterval = 60

    static func isStale(_ loadedAt: Date?) -> Bool {
        loadedAt.map { Date().timeIntervalSince($0) > maxAge } ?? true
    }
}

/// Loading, error and empty states in plain words.
struct StatusView: View {
    enum State {
        case loading
        case failed(String, retry: () -> Void)
        case empty(String)
    }

    let state: State

    var body: some View {
        VStack(spacing: 28) {
            switch state {
            case .loading:
                ProgressView().scaleEffect(1.4)
            case .failed(let message, let retry):
                Image(systemName: "wifi.exclamationmark").font(.system(size: 64)).foregroundStyle(Color.usher)
                Text(message)
                    .font(.system(size: 30, weight: .medium))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 1000)
                Button("Try again", action: retry)
                    .buttonStyle(ActionButtonStyle(prominent: true))
            case .empty(let message):
                Text(message)
                    .font(.system(size: 30, weight: .medium))
                    .foregroundStyle(Color.usher)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
