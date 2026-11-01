import FlexarrKit
import SwiftUI

enum TabID: Hashable {
    case home
    case libraries
    case downloads
    case search
    case settings
}

/// Home, Libraries, Downloads, Search and Settings along the bottom (a sidebar on iPad).
struct MainTabView: View {
    @Environment(AppModel.self) private var model
    @Environment(DownloadStore.self) private var downloads
    @Environment(TVRemote.self) private var remote
    @Environment(\.scenePhase) private var scenePhase
    @State private var selection: TabID = .home

    var body: some View {
        TabView(selection: $selection) {
            Tab(value: TabID.home) {
                TabStack(handlesDeepLinks: true) {
                    if model.isOffline { OfflineView(title: "Home") { selection = .downloads } } else { HomeView() }
                }
            } label: {
                // The Flexarr mark stands in for a house.
                Label { Text("Home") } icon: { Image(uiImage: BrandMark.tabImage) }
            }
            Tab("Libraries", systemImage: Symbol.libraries, value: TabID.libraries) {
                TabStack {
                    if model.isOffline { OfflineView(title: "Libraries") { selection = .downloads } } else { LibrariesView() }
                }
            }
            Tab("Downloads", systemImage: Symbol.downloads, value: TabID.downloads) {
                TabStack { DownloadsView() }
            }
            Tab("Search", systemImage: Symbol.search, value: TabID.search, role: .search) {
                TabStack {
                    if model.isOffline { OfflineView(title: "Search") { selection = .downloads } } else { SearchView() }
                }
            }
            // The kids app has no Settings: a child's choices are a parent's to make.
            if !model.isKids {
                Tab("Settings", systemImage: Symbol.settings, value: TabID.settings) {
                    TabStack { SettingsView() }
                }
            }
        }
        .tabViewStyle(.sidebarAdaptable)
        // Opened offline: straight to what can be watched, if there's anything. Going offline later leaves you where you are.
        .onAppear { if model.isOffline, downloads.hasAnything { selection = .downloads } }
        // Said aloud too, as the bar comes and goes.
        .onChange(of: model.isOffline) { _, offline in
            AccessibilityNotification.Announcement(offline ? "Offline" : "Back online").post()
        }
        // The libraries load now, and again once the server's back after offline mode.
        .task(id: model.isOffline) {
            guard !model.isOffline else { return }
            await model.loadLibraries()
            // Opened with no connection the check didn't catch: straight to what can be watched.
            if model.libraries.isEmpty, model.librariesError != nil, !downloads.downloads.isEmpty { selection = .downloads }
        }
        // What the TVs are playing, followed while the app is in front.
        .task(id: "\(model.credentials?.address.absoluteString ?? "")#\(model.currentProfile?.id ?? "")#\(scenePhase == .active)") {
            remote.use(model.client)
            remote.profileId = model.currentProfile?.id
            remote.isActive = scenePhase == .active
            guard scenePhase == .active else { return }
            await remote.follow()
        }
        // The remote for a TV, over every tab; and a tap on it (or a command that failed) felt.
        .sheet(isPresented: Bindable(remote).showsRemote) {
            TVRemoteView()
                .presentationDetents([.large])
                .presentationDragIndicator(.hidden)
                .presentationBackground(Color.room)
        }
        .sensoryFeedback(.impact(weight: .light), trigger: remote.taps)
        .sensoryFeedback(.error, trigger: remote.failures)
        .onChange(of: model.pendingRoute) { _, route in
            if route != nil { selection = .home }
        }
        .onChange(of: model.pendingSearch) { _, words in
            if words != nil { selection = .search }
        }
        // Opening the app from a notification can set it before the tabs are up.
        .onChange(of: model.pendingSettings, initial: true) { _, wanted in
            guard wanted else { return }
            model.pendingSettings = false
            if !model.isKids { selection = .settings }
        }
    }
}

/// A navigation stack per tab with the shared destinations.
struct TabStack<Content: View>: View {
    @Environment(AppModel.self) private var model
    @Environment(TVRemote.self) private var remote
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
        .safeAreaInset(edge: .top, spacing: 0) {
            if model.isOffline { OfflineBar() }
        }
        .animation(.easeInOut(duration: 0.25), value: model.isOffline)
        // Music carries on under every screen; the bar sits above the tab bar and opens Now Playing.
        // So does a TV's: its bar opens the remote.
        .safeAreaInset(edge: .bottom) {
            VStack(spacing: 0) {
                // The remote has its own (on iPad its sheet doesn't cover the bars: one notice, not two).
                // Neither while offline: what the TVs are doing can't be known, or changed.
                if !remote.showsRemote, !model.isOffline { RemoteNoticeView() }
                if !model.isOffline { TVPlayingBar() }
                NowPlayingBar()
            }
        }
        .environment(router)
        .onChange(of: model.pendingRoute) { _, _ in consumeDeepLink() }
        .onAppear(perform: consumeDeepLink)
    }

    private func consumeDeepLink() {
        guard handlesDeepLinks, let route = model.pendingRoute else { return }
        model.pendingRoute = nil
        // A server without a watchlist (before 0.56) has nothing to open.
        if case .watchlist = route, !model.hasWatchlist { return }
        // Already showing it (the widget tapped while its page is open): not a second copy for Back to land on.
        if router.path.last == route { return }
        router.push(route)
    }

    @ViewBuilder
    private func destination(_ route: Route) -> some View {
        switch route {
        case .item(let item) where item.kind.isMusic:
            MusicDestination(item: item)
        case .item(let item) where item.kind == .audiobook:
            BookView(id: item.id, preview: item)
        case .item(let item) where item.kind == .podcast:
            PodcastView(id: item.id, preview: item)
        case .item(let item):
            DetailView(id: item.id, preview: item)
        case .itemID(let id):
            DetailView(id: id, preview: nil)
        case .person(let id, let name):
            PersonView(id: id, name: name)
        case .library(let library, _, _) where library.type == .unknown:
            // A library of a type this app doesn't know (from a newer server): nothing to show it with.
            EmptyView()
        case .library(let library, _, _) where library.type == .music:
            MusicLibraryView(library: library)
        case .library(let library, _, let genre) where library.type == .audiobooks:
            // A genre opens a screen of its own, above the library's (Back goes to its genres).
            BooksLibraryView(library: library, genre: genre)
        case .bookSeries(let id, let name):
            BookSeriesView(id: id, name: name)
        case .library(let library, _, _) where library.type == .podcasts:
            PodcastsView()
        case .library(let library, let sort, let genre):
            LibraryView(library: library, initialSort: sort, initialGenre: genre)
        case .playlist(let playlist):
            PlaylistView(playlist: playlist)
        case .podcasts:
            PodcastsView()
        case .podcastBrowse:
            PodcastBrowseView()
        case .liveTv:
            LiveTvView()
        case .watchlist:
            WatchlistView()
        case .comingSoon:
            ComingSoonView()
        case .external, .genre:
            // Discover is on the Apple TV and the web; nothing on iPhone or iPad goes here.
            EmptyView()
        }
    }
}

/// The libraries this profile can see, each opening its poster grid, and Podcasts.
struct LibrariesView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router

    var body: some View {
        Group {
            if model.libraries.isEmpty {
                if let error = model.librariesError {
                    StatusView(state: .failed(error) { Task { await model.loadLibraries() } })
                } else {
                    StatusView(state: .loading)
                }
            } else {
                List {
                    // Not for a profile that hid it from the menu: Home's row and links still open it.
                    if model.hasWatchlist && !model.hidesFromMenu(.watchlist) {
                        Section {
                            row("Watchlist", icon: Symbol.watchlist) { router.push(.watchlist) }
                        }
                    }
                    // In the profile's order (Settings › Library order). Podcasts are one place however many libraries hold
                    // them, and say how to set them up when none does.
                    ForEach(LibraryMenuEntry.ordered(model: model)) { entry in
                        row(entry.title, icon: entry.symbol) {
                            switch entry {
                            case .library(let library): router.push(.library(library, .title))
                            case .podcasts: router.push(.podcasts)
                            case .liveTv: router.push(.liveTv)
                            }
                        }
                    }
                }
                .scrollContentBackground(.hidden)
                .refreshable { await model.loadLibraries() }
            }
        }
        .background(RoomBackground(ambient: nil))
        .navigationTitle("Libraries")
    }

    private func row(_ name: String, icon: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 14) {
                Image(systemName: icon)
                    .font(.title3)
                    .foregroundStyle(Color.silver)
                    .frame(width: 32)
                Text(name)
                    .font(.body.weight(.semibold))
                    .foregroundStyle(Color.screen)
                Spacer()
                Image(systemName: "chevron.right")
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Color.usher)
            }
            .padding(.vertical, 8)
            .contentShape(Rectangle())
        }
        .listRowBackground(Color.seat)
    }
}
