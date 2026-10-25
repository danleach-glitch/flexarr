import FlexarrKit
import SwiftUI
import UIKit

enum TabID: Hashable {
    case home
    case library(String)
    case search
    case settings
    /// Only while music plays.
    case nowPlaying
}

/// tvOS top tabs: Home, one per library, Search and Settings, with who's watching in the top right
/// corner when there are several profiles.
struct MainTabView: View {
    @Environment(AppModel.self) private var model
    @State private var selection: TabID = .home
    @State private var choosingProfile = false

    var body: some View {
        TabView(selection: $selection) {
            Tab(value: TabID.home) {
                TabStack(handlesDeepLinks: true) { HomeView() }
            } label: {
                Text("Home")
            }
            ForEach(model.libraries) { library in
                Tab(value: TabID.library(library.id)) {
                    TabStack {
                        if library.type == .music {
                            MusicLibraryView(library: library)
                        } else {
                            LibraryView(library: library)
                        }
                    }
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
            // Music that's playing gets a tab of its own, as the Music app does: the surest way to reach it with
            // the remote from anywhere.
            if model.music.current != nil {
                Tab(value: TabID.nowPlaying) {
                    NowPlayingView()
                } label: {
                    Label("Now Playing", systemImage: "waveform")
                }
            }
        }
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
        .task { await model.loadLibraries() }
        .onChange(of: model.pendingRoute) { _, route in
            if route != nil { selection = .home }
        }
    }
}

/// Who's watching, at the right end of the tab bar: press right from the last tab to reach it.
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
        // Music carries on under every screen; the bar says what's playing and opens Now Playing.
        .safeAreaInset(edge: .bottom) { NowPlayingBar() }
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
        case .item(let item) where item.kind.isMusic:
            MusicDestination(item: item)
        case .item(let item):
            DetailView(id: item.id, preview: item)
        case .itemID(let id):
            DetailView(id: id, preview: nil)
        case .person(let id, let name):
            PersonView(id: id, name: name)
        case .library(let library, _) where library.type == .music:
            MusicLibraryView(library: library)
        case .library(let library, let sort):
            LibraryView(library: library, initialSort: sort)
        case .playlist(let playlist):
            PlaylistView(playlist: playlist)
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
