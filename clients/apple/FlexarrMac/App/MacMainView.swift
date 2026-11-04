import FlexarrKit
import SwiftUI

/// A place in the Mac's sidebar.
enum SidebarItem: Hashable {
    case home
    case watchlist
    case entry(LibraryMenuEntry)
    case downloads
    case search
    case settings
}

/// What the Mac's sidebar and menus can ask of the window (FlexarrCommands): go to a place, go back.
@MainActor @Observable
final class MacNavigation {
    static let shared = MacNavigation()
    var selection: SidebarItem? = .home
    /// Each place keeps its own stack, so going back to it lands where it was.
    private(set) var routers: [SidebarItem: Router] = [:]

    func router(for item: SidebarItem) -> Router {
        if let router = routers[item] { return router }
        let router = Router()
        routers[item] = router
        return router
    }

    var canGoBack: Bool { selection.map { !(routers[$0]?.path.isEmpty ?? true) } ?? false }

    func goBack() {
        guard let selection, let router = routers[selection], !router.path.isEmpty else { return }
        router.path.removeLast()
    }

    /// A new profile or server: every place starts again at its top.
    func reset() {
        routers = [:]
        selection = .home
    }
}

/// The Mac's window once signed in: the sidebar (Home, Watchlist, each library in the profile's order, Downloads,
/// Search, Settings) and the place chosen there, each in its own navigation stack. The iPad's tab bar becomes a sidebar
/// the same way; on the Mac the libraries are in it too, as Apple's own apps list theirs.
struct MacMainView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @State private var navigation = MacNavigation.shared

    var body: some View {
        NavigationSplitView {
            List(selection: $navigation.selection) {
                Section {
                    Label { Text(t(Words.home)) } icon: { BrandMark(size: 18) }
                        .tag(SidebarItem.home)
                    if model.hasWatchlist && !model.hidesFromMenu(.watchlist) {
                        Label(t(Words.watchlist), systemImage: Symbol.watchlist).tag(SidebarItem.watchlist)
                    }
                    Label(t(Words.search), systemImage: Symbol.search).tag(SidebarItem.search)
                }
                if !model.isOffline {
                    Section(t(Words.libraries)) {
                        ForEach(LibraryMenuEntry.ordered(model: model)) { entry in
                            Label(entry.title, systemImage: entry.symbol).tag(SidebarItem.entry(entry))
                        }
                    }
                }
                Section {
                    Label(t(Words.downloads), systemImage: Symbol.downloads).tag(SidebarItem.downloads)
                    // The kids app has no Settings: a child's choices are a parent's to make.
                    if !model.isKids {
                        Label(t(Words.settings), systemImage: Symbol.settings).tag(SidebarItem.settings)
                    }
                }
            }
            .navigationSplitViewColumnWidth(min: 190, ideal: 220, max: 300)
        } detail: {
            if let selection = navigation.selection {
                MacStack(item: selection, router: navigation.router(for: selection)) {
                    root(for: selection)
                }
                .id(selection)
            } else {
                Color.room
            }
        }
        .onAppear {
            navigation.reset()
            if model.isOffline, downloads.hasAnything { navigation.selection = .downloads }
        }
        .onChange(of: model.isOffline) { _, offline in
            AccessibilityNotification.Announcement(offline ? t("Offline") : t("Back online")).post()
        }
        .task(id: model.isOffline) {
            guard !model.isOffline else { return }
            await model.loadLibraries()
            if model.libraries.isEmpty, model.librariesError != nil, !downloads.downloads.isEmpty { navigation.selection = .downloads }
        }
        .sheet(item: Bindable(model).addingToPlaylist) { AddToPlaylistSheet(request: $0).frame(minWidth: 420, minHeight: 480) }
        .onChange(of: model.pendingRoute) { _, route in
            if route != nil { navigation.selection = .home }
        }
        .onChange(of: model.pendingSearch) { _, words in
            if words != nil { navigation.selection = .search }
        }
        .onChange(of: model.pendingSettings, initial: true) { _, wanted in
            guard wanted else { return }
            model.pendingSettings = false
            if !model.isKids { navigation.selection = .settings }
        }
    }

    @ViewBuilder
    private func root(for item: SidebarItem) -> some View {
        switch item {
        case .home:
            if model.isOffline { OfflineView(title: t(Words.home)) { navigation.selection = .downloads } } else { HomeView() }
        case .watchlist:
            WatchlistView()
        case .entry(.library(let library)):
            RouteDestination(route: .library(library, .title))
        case .entry(.podcasts):
            PodcastsView()
        case .entry(.liveTv):
            LiveTvView()
        case .downloads:
            DownloadsView()
        case .search:
            if model.isOffline { OfflineView(title: t(Words.search)) { navigation.selection = .downloads } } else { SearchView() }
        case .settings:
            SettingsView()
        }
    }
}

/// One sidebar place's navigation stack, with the shared destinations, the offline bar and the music bar.
struct MacStack<Content: View>: View {
    @Environment(AppModel.self) private var model
    let item: SidebarItem
    let router: Router
    @ViewBuilder var content: () -> Content

    var body: some View {
        @Bindable var router = router
        NavigationStack(path: $router.path) {
            content()
                .navigationDestination(for: Route.self) { RouteDestination(route: $0) }
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            if model.isOffline { OfflineBar() }
        }
        .animation(.easeInOut(duration: 0.25), value: model.isOffline)
        // Music carries on under every place; the bar opens Now Playing.
        .safeAreaInset(edge: .bottom) { NowPlayingBar() }
        .environment(router)
        .onChange(of: model.pendingRoute) { _, _ in consumeDeepLink() }
        .onAppear(perform: consumeDeepLink)
    }

    private func consumeDeepLink() {
        guard item == .home, let route = model.pendingRoute else { return }
        model.pendingRoute = nil
        if case .watchlist = route, !model.hasWatchlist { return }
        if router.path.last == route { return }
        router.push(route)
    }
}
