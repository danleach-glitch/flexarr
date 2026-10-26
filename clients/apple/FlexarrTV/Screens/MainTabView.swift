import FlexarrKit
import SwiftUI
import UIKit

enum TabID: Hashable {
    case home
    case library(String)
    case liveTv
    case search
    case settings
    /// Only while music plays.
    case nowPlaying
}

/// tvOS top tabs: Home, one per library, Search and a gear for Settings, with who's watching in the top right
/// corner when there are several profiles.
struct MainTabView: View {
    @Environment(AppModel.self) private var model
    @State private var selection: TabID = .home
    @State private var choosingProfile = false
    /// Home's screens, here so Menu on the tab bar can go back through them (see below).
    @State private var homeRouter = Router()
    @State private var librariesLoaded = false
    /// Where a link (the Top Shelf) opens its title: on its library's tab, so Home stays Home.
    @State private var linkTab: TabID?
    /// Just after a link picks a tab: as the app starts, focus can land on the Home tab a moment later and, since the
    /// tab bar selects what it focuses, take the selection back to Home with the title hidden on the other tab.
    @State private var linkHold: (tab: TabID, until: Date)?
    @AppStorage(TabBarEntry.storageKey) private var tabBarOrder = ""

    var body: some View {
        TabView(selection: $selection) {
            Tab(value: TabID.home) {
                TabStack(router: homeRouter, opensLinks: linkTab == .home) {
                    HomeView().environment(\.opensTitlesOnLibraryTab, true)
                }
            } label: {
                Text("Home")
            }
            // The libraries and Live TV, in the order chosen in Settings › Tab bar.
            ForEach(TabBarEntry.ordered(model: model, stored: tabBarOrder)) { entry in
                Tab(value: entry.tab) {
                    switch entry {
                    case .library(let library):
                        TabStack(opensLinks: linkTab == entry.tab) {
                            if library.type == .music {
                                MusicLibraryView(library: library, showsTitle: false)
                            } else {
                                LibraryTab(library: library)
                            }
                        }
                    case .liveTv:
                        TabStack { LiveTvView(showsTitle: false) }
                    }
                } label: {
                    Text(entry.title)
                }
            }
            // An icon, like the gear, so the bar stays short.
            Tab(value: TabID.search, role: .search) {
                TabStack { SearchView() }
            } label: {
                Image(systemName: "magnifyingglass")
                    .accessibilityLabel("Search")
            }
            // Music that's playing gets a tab of its own, as the Music app does: the surest way to reach it with
            // the remote from anywhere.
            if model.music.current != nil {
                Tab(value: TabID.nowPlaying) {
                    NowPlayingView()
                } label: {
                    Label("Now Playing", systemImage: "waveform")
                }
            }
            // A gear rather than a word, so the bar stays short.
            Tab(value: TabID.settings) {
                TabStack { SettingsView() }
            } label: {
                Image(systemName: "gearshape")
                    .accessibilityLabel("Settings")
            }
        }
        // Menu on the tab bar leaves the app. With screens open on Home, go back through them first.
        .onExitCommand(perform: selection == .home && !homeRouter.path.isEmpty ? { homeRouter.path.removeLast() } : nil)
        // The remote's play/pause button runs the music from any screen (the video player takes it over while it's up).
        .onPlayPauseCommand { if model.music.current != nil { model.music.togglePlayPause() } }
        .onChange(of: model.music.current == nil) { _, stopped in
            if stopped, selection == .nowPlaying { selection = .home }
        }
        .overlay(alignment: .topTrailing) {
            if model.profiles.count > 1, let current = model.currentProfile {
                ProfileCornerButton(profile: current) { choosingProfile = true }
                    .frame(width: ProfileCornerButton.size, height: ProfileCornerButton.size)
                    // Level with the tab bar, which sits 46pt from the top and is 68pt tall.
                    .padding(.top, 46 + (68 - ProfileCornerButton.size) / 2)
                    .ignoresSafeArea(edges: .top)
            }
        }
        .fullScreenCover(isPresented: $choosingProfile) {
            ProfilePickerView(marksCurrent: true)
        }
        .task {
            await model.loadLibraries()
            librariesLoaded = true
        }
        .onAppear(perform: routeLink)
        .onChange(of: model.pendingRoute) { _, _ in routeLink() }
        .onChange(of: librariesLoaded) { _, _ in routeLink() }
        .onChange(of: selection) { _, tab in
            guard let hold = linkHold, tab != hold.tab else { return }
            if Date() < hold.until { selection = hold.tab } else { linkHold = nil }
        }
        .onChange(of: model.pendingSearch) { _, words in
            if words != nil { selection = .search }
        }
    }

    private var tabLibraries: [Library] { TabBarEntry.libraries(model: model) }

    /// Picks the tab a link's title opens on and goes to it; that tab's stack then shows the title.
    private func routeLink() {
        guard let route = model.pendingRoute else {
            linkTab = nil
            return
        }
        var tab = TabID.home
        if case .item(let item) = route {
            // Until the libraries are in, its tab may not be there yet.
            guard librariesLoaded else { return }
            if tabLibraries.contains(where: { $0.id == item.libraryId }) { tab = .library(item.libraryId) }
        }
        linkTab = tab
        selection = tab
        linkHold = (tab, Date().addingTimeInterval(1.5))
    }
}

/// Who's watching, at the right end of the tab bar: press right from the last tab to reach it. Pressing it opens
/// "Who's watching?" to switch profile.
/// A UIKit control, because tvOS moves focus between it and the tab bar's UIKit buttons both ways,
/// whereas a SwiftUI button keeps a press left and drops it. It shows and is focusable only while
/// the tab bar is, so focus starts on the tabs and a press up from a screen goes to them.
private struct ProfileCornerButton: UIViewRepresentable {
    static let size: CGFloat = 64

    @Environment(AppModel.self) private var model
    let profile: Profile
    let action: () -> Void

    func makeUIView(context: Context) -> Control { Control() }

    func updateUIView(_ control: Control, context: Context) {
        control.action = action
        control.accessibilityLabel = "Profile: \(profile.name)"
        control.avatar.rootView = AnyView(ProfileAvatar(profile: profile, size: Self.size).environment(model))
    }

    final class Control: UIControl {
        let avatar = UIHostingController(rootView: AnyView(EmptyView()))
        var action: (() -> Void)?
        private let ring = CAShapeLayer()
        private var tabBarFocused = false
        /// Pressed, so the picker is up: stay focusable for focus to come back here when it closes.
        private var awaitingReturn = false
        private var observer: NSObjectProtocol?

        override init(frame: CGRect) {
            super.init(frame: frame)
            avatar.view.backgroundColor = .clear
            avatar.view.isUserInteractionEnabled = false
            // The control sits inside the screen's top safe area; left to SwiftUI, the hosted picture would be
            // laid out below that inset and hang beneath the ring, which is drawn on the control's bounds.
            avatar.safeAreaRegions = []
            addSubview(avatar.view)
            ring.fillColor = nil
            ring.strokeColor = UIColor(Color.screen).cgColor
            ring.lineWidth = 4
            ring.opacity = 0
            layer.addSublayer(ring)
            layer.shadowColor = UIColor.black.cgColor
            layer.shadowOffset = CGSize(width: 0, height: 8)
            layer.shadowRadius = 16
            layer.shadowOpacity = 0
            isAccessibilityElement = true
            accessibilityTraits = .button
            addTarget(self, action: #selector(triggered), for: .primaryActionTriggered)
        }

        @available(*, unavailable)
        required init?(coder: NSCoder) { fatalError() }

        override func layoutSubviews() {
            super.layoutSubviews()
            avatar.view.frame = bounds
            ring.path = UIBezierPath(ovalIn: bounds.insetBy(dx: -7, dy: -7)).cgPath
        }

        override var canBecomeFocused: Bool { tabBarFocused || awaitingReturn || isFocused }

        override func didUpdateFocus(in context: UIFocusUpdateContext, with coordinator: UIFocusAnimationCoordinator) {
            super.didUpdateFocus(in: context, with: coordinator)
            let focused = isFocused
            coordinator.addCoordinatedAnimations {
                self.transform = focused ? CGAffineTransform(scaleX: 1.15, y: 1.15) : .identity
                self.ring.opacity = focused ? 1 : 0
                self.layer.shadowOpacity = focused ? 0.5 : 0
            }
        }

        override func pressesBegan(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
            guard presses.contains(where: { $0.type == .select }) else { return super.pressesBegan(presses, with: event) }
            UIView.animate(withDuration: 0.12) { self.transform = CGAffineTransform(scaleX: 1.05, y: 1.05) }
        }

        override func pressesEnded(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
            guard presses.contains(where: { $0.type == .select }) else { return super.pressesEnded(presses, with: event) }
            UIView.animate(withDuration: 0.12) { self.transform = CGAffineTransform(scaleX: 1.15, y: 1.15) }
            sendActions(for: .primaryActionTriggered)
        }

        override func pressesCancelled(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
            guard presses.contains(where: { $0.type == .select }) else { return super.pressesCancelled(presses, with: event) }
            UIView.animate(withDuration: 0.12) { self.transform = self.isFocused ? CGAffineTransform(scaleX: 1.15, y: 1.15) : .identity }
        }

        @objc private func triggered() {
            awaitingReturn = true
            action?()
        }

        override func didMoveToWindow() {
            super.didMoveToWindow()
            if window == nil {
                observer.map(NotificationCenter.default.removeObserver)
                observer = nil
            } else if observer == nil {
                observer = NotificationCenter.default.addObserver(
                    forName: UIFocusSystem.didUpdateNotification, object: nil, queue: .main
                ) { [weak self] _ in
                    MainActor.assumeIsolated { self?.focusDidUpdate() }
                }
            }
        }

        private func focusDidUpdate() {
            guard let bar = window.flatMap(Self.tabBar(in:)) else { return }
            let next = UIFocusSystem.focusSystem(for: self)?.focusedItem as? UIView
            tabBarFocused = next?.isDescendant(of: bar) ?? false
            if next === self || tabBarFocused { awaitingReturn = false }
            // tvOS 18 slides the tab bar away once focus moves into a screen (tvOS 26 keeps it);
            // look once its animation has been set up.
            DispatchQueue.main.async { [weak self, weak bar] in
                guard let self, let bar else { return }
                let shown = Self.isShowing(bar)
                UIView.animate(withDuration: 0.25) { self.alpha = shown ? 1 : 0 }
            }
        }

        private static func tabBar(in view: UIView) -> UITabBar? {
            (view as? UITabBar) ?? view.subviews.lazy.compactMap(tabBar(in:)).first
        }

        private static func isShowing(_ bar: UITabBar) -> Bool {
            let chain = sequence(first: bar as UIView, next: { $0.superview })
            guard chain.allSatisfy({ !$0.isHidden && $0.alpha > 0.01 }) else { return false }
            return bar.convert(bar.bounds, to: nil).maxY > 0
        }
    }
}

/// A navigation stack per tab with the shared destinations.
struct TabStack<Content: View>: View {
    @Environment(AppModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase
    @State private var ownRouter = Router()
    /// Home's, owned by `MainTabView` and used as it is (not copied into state here), so the stack and Menu's check
    /// on the tab bar always look at the same screens.
    private let sharedRouter: Router?
    /// This tab is where the pending link opens (see `MainTabView.routeLink`).
    var opensLinks: Bool
    @ViewBuilder var content: () -> Content
    /// What's open came from a link (the Top Shelf), not from browsing.
    @State private var openedFromLink = false

    init(router: Router? = nil, opensLinks: Bool = false, @ViewBuilder content: @escaping () -> Content) {
        sharedRouter = router
        self.opensLinks = opensLinks
        self.content = content
    }

    private var router: Router { sharedRouter ?? ownRouter }

    var body: some View {
        @Bindable var router = router
        NavigationStack(path: $router.path) {
            content()
                .navigationDestination(for: Route.self) { route in
                    destination(route)
                }
        }
        // Music carries on under every screen; the bar says what's playing and opens Now Playing.
        .safeAreaInset(edge: .bottom) { NowPlayingBar() }
        .environment(router)
        .onChange(of: model.pendingRoute) { _, _ in consumeDeepLink() }
        .onChange(of: opensLinks) { _, _ in consumeDeepLink() }
        .onAppear(perform: consumeDeepLink)
        .onChange(of: router.path.isEmpty) { _, empty in
            if empty { openedFromLink = false }
        }
        // A title opened from the Top Shelf is a visit: leaving the app puts the tab back as it was, so coming back
        // doesn't find the title still in its place.
        .onChange(of: scenePhase) { _, phase in
            guard phase == .background, openedFromLink else { return }
            router.path = []
            openedFromLink = false
        }
    }

    private func consumeDeepLink() {
        guard opensLinks, let route = model.pendingRoute else { return }
        // In place of whatever was open, so Menu comes straight back to the tab rather than through old links.
        router.path = [route]
        openedFromLink = true
        model.pendingRoute = nil
    }

    @ViewBuilder
    private func destination(_ route: Route) -> some View {
        switch route {
        case .item(let item) where item.kind.isMusic:
            MusicDestination(item: item)
        case .item(let item):
            DetailView(id: item.id, preview: item)
        case .itemID(let id):
            DetailView(id: id, preview: nil)
        case .person(let id, let name):
            PersonView(id: id, name: name)
        case .library(let library, _, _) where library.type == .music:
            MusicLibraryView(library: library)
        case .library(let library, let sort, let genre):
            LibraryView(library: library, initialSort: sort, initialGenre: genre)
        case .playlist(let playlist):
            PlaylistView(playlist: playlist)
        case .liveTv:
            LiveTvView()
        case .podcasts, .podcastBrowse:
            // Podcasts are on iPhone, iPad and the web; nothing on the Apple TV goes here.
            EmptyView()
        }
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

extension EnvironmentValues {
    /// On Home: a long-press's "Go to details" opens the title on its library's tab, as the Top Shelf does, so the
    /// title (and what's played from it) doesn't take Home's place.
    @Entry var opensTitlesOnLibraryTab = false
}
