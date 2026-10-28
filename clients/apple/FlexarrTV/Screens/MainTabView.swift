import FlexarrKit
import SwiftUI
import UIKit

enum TabID: Hashable {
    case home
    case library(String)
    case liveTv
    case discover
    case search
    case settings
    /// Who's watching: selecting it opens "Who's watching?" rather than a screen of its own.
    case profile
    /// Only while music plays.
    case nowPlaying
}

/// tvOS sidebar: who's watching (with several profiles), Home, the libraries and Live TV, then Discover, Search and Settings.
/// A sidebar rather than tabs along the top, which run out of room with more than a few libraries.
struct MainTabView: View {
    @Environment(AppModel.self) private var model
    @State private var selection: TabID = .home
    @State private var choosingProfile = false
    /// Home's screens, here so Menu on the sidebar can go back through them (see below).
    @State private var homeRouter = Router()
    @State private var librariesLoaded = false
    /// Where a link (the Top Shelf) opens its title: on its library's tab, so Home stays Home.
    @State private var linkTab: TabID?
    /// Just after a link picks a tab: as the app starts, focus can land on the Home tab a moment later and, since a
    /// tab bar selects what it focuses (tvOS 18 shows one when the sidebar is collapsed), take the selection back to
    /// Home with the title hidden on the other tab.
    @State private var linkHold: (tab: TabID, until: Date)?
    /// As the app opens, Home's Play takes focus from the sidebar, so the sidebar closes and Home is ready to browse.
    /// Not when a link opens a title, or once another tab is chosen.
    @State private var startFocus = true
    @AppStorage(TabBarEntry.storageKey) private var tabBarOrder = ""

    var body: some View {
        TabView(selection: $selection) {
            // Who's watching, at the top of the sidebar as in the TV app.
            if model.profiles.count > 1, let current = model.currentProfile {
                Tab(value: TabID.profile) {
                    Color.clear
                } label: {
                    SidebarProfileLabel(profile: current)
                }
            }
            Tab(value: TabID.home) {
                TabStack(router: homeRouter, opensLinks: linkTab == .home) {
                    HomeView()
                        .environment(\.opensTitlesOnLibraryTab, true)
                        .environment(\.focusesContent, $startFocus)
                }
            } label: {
                Label("Home", systemImage: "house")
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
            // The libraries and Live TV, in the order chosen in Settings › Sidebar: however many there are, the
            // sidebar scrolls rather than pushing Search and Settings out of reach as a tab bar would.
            TabSection("Libraries") {
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
                        Label(entry.title, systemImage: entry.symbol)
                    }
                }
            }
            // Discover, Search and Settings after the libraries. In a section of their own, since the sidebar puts tabs
            // outside a section above them.
            TabSection {
                Tab(value: TabID.discover) {
                    TabStack { DiscoverView() }
                } label: {
                    Label("Discover", systemImage: "sparkles")
                }
                Tab(value: TabID.search, role: .search) {
                    TabStack { SearchView() }
                } label: {
                    Label("Search", systemImage: "magnifyingglass")
                }
                Tab(value: TabID.settings) {
                    TabStack { SettingsView() }
                } label: {
                    Label("Settings", systemImage: "gearshape")
                }
            } header: {
                // A rule between them and the libraries: the sidebar takes a section's heading as plain text, so a
                // Divider, a shape or a strikethrough here would be dropped; a run of box-drawing lines draws it.
                Text(verbatim: String(repeating: "\u{2500}", count: 12))
            }
        }
        .tabViewStyle(.sidebarAdaptable)
        .modifier(SidebarVersion())
        // Menu on the sidebar leaves the app. With screens open on Home, go back through them first.
        .onExitCommand(perform: selection == .home && !homeRouter.path.isEmpty ? { homeRouter.path.removeLast() } : nil)
        // The remote's play/pause button runs the music from any screen (the video player takes it over while it's up).
        .onPlayPauseCommand { if model.music.current != nil { model.music.togglePlayPause() } }
        .onChange(of: model.music.current == nil) { _, stopped in
            if stopped, selection == .nowPlaying { selection = .home }
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
        .onChange(of: selection) { old, tab in
            guard tab != .profile else {
                choosingProfile = true
                selection = old
                return
            }
            if tab != .home { startFocus = false }
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
        startFocus = false
        linkHold = (tab, Date().addingTimeInterval(1.5))
    }
}

/// Who's watching in the sidebar: their picture, or their initial on their colour, as on "Who's watching?". The
/// sidebar draws a tab's label as text and an image, not views, so the avatar goes in as a round picture.
private struct SidebarProfileLabel: View {
    @Environment(AppModel.self) private var model
    let profile: Profile
    @State private var picture: UIImage?

    private static let size: CGFloat = 44

    var body: some View {
        Label {
            Text(profile.name)
        } icon: {
            Image(uiImage: picture ?? initial).renderingMode(.original)
        }
        .task(id: profile.image) { picture = await loadPicture() }
    }

    private var initial: UIImage {
        let renderer = ImageRenderer(content: ZStack {
            Circle().fill(Color(rgb: RGB(hex: profile.color) ?? .silver))
            Text(profile.initial)
                .font(.marquee(Self.size * 0.46, weight: .heavy))
                .foregroundStyle(Color.room)
        }.frame(width: Self.size, height: Self.size))
        renderer.scale = 2
        return renderer.uiImage ?? UIImage()
    }

    private func loadPicture() async -> UIImage? {
        guard let url = model.client?.imageURL(profile.image, width: Int(Self.size * 2)),
              let image = await ImageLoader.shared.load(url) else { return nil }
        // Fill the circle from the middle, as the avatar does, whatever the picture's shape.
        let side = Self.size
        let format = UIGraphicsImageRendererFormat()
        format.scale = 2
        return UIGraphicsImageRenderer(size: CGSize(width: side, height: side), format: format).image { _ in
            UIBezierPath(ovalIn: CGRect(x: 0, y: 0, width: side, height: side)).addClip()
            let scale = max(side / image.size.width, side / image.size.height)
            let drawn = CGSize(width: image.size.width * scale, height: image.size.height * scale)
            image.draw(in: CGRect(x: (side - drawn.width) / 2, y: (side - drawn.height) / 2, width: drawn.width, height: drawn.height))
        }
    }
}

/// The logo and the app's version, quietly at the foot of the sidebar. The footer is new in tvOS 27; before that, it's only on
/// Settings.
private struct SidebarVersion: ViewModifier {
    func body(content: Content) -> some View {
        if #available(tvOS 27, *) {
            content.tabViewSidebarFooter {
                // The logo's middle on the version's.
                HStack(alignment: .center, spacing: 12) {
                    HStack(alignment: .center, spacing: 6) {
                        BrandMark(size: 26)
                        Text("flexarr")
                            .font(.marquee(22, weight: .black))
                            .foregroundStyle(Color.screen.opacity(0.8))
                    }
                    Text(Bundle.main.shortVersion)
                        .font(.system(size: 20, weight: .medium))
                        .foregroundStyle(Color.usher.opacity(0.7))
                        .monospacedDigit()
                }
                .accessibilityElement(children: .combine)
                .accessibilityLabel("Flexarr \(Bundle.main.shortVersion)")
            }
        } else {
            content
        }
    }
}

/// A navigation stack per tab with the shared destinations.
struct TabStack<Content: View>: View {
    @Environment(AppModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase
    @State private var ownRouter = Router()
    /// Home's, owned by `MainTabView` and used as it is (not copied into state here), so the stack and Menu's check
    /// on the sidebar always look at the same screens.
    private let sharedRouter: Router?
    /// This tab is where the pending link opens (see `MainTabView.routeLink`).
    var opensLinks: Bool
    @ViewBuilder var content: () -> Content
    /// What's open came from a link (the Top Shelf), not from browsing.
    @State private var openedFromLink = false
    /// The title a link just opened should take focus (see `focusesPlay`).
    @State private var linkFocus = false

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
                    // Given again here: the sidebar shows pushed screens outside the stack's environment.
                    destination(route).environment(router).environment(\.focusesPlay, $linkFocus)
                }
        }
        // Music carries on under every screen; the bar says what's playing and opens Now Playing.
        .safeAreaInset(edge: .bottom) { NowPlayingBar() }
        .environment(router)
        .onChange(of: model.pendingRoute) { _, _ in consumeDeepLink() }
        .onChange(of: opensLinks) { _, _ in consumeDeepLink() }
        .onAppear(perform: consumeDeepLink)
        .onChange(of: router.path.isEmpty) { _, empty in
            if empty {
                openedFromLink = false
                linkFocus = false
            }
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
        linkFocus = true
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
        case .external(let title):
            ExternalTitleView(preview: title)
        case .genre(let name):
            GenreView(name: name)
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
    /// Set as a link (the Top Shelf) opens a title: its Play takes focus as it appears, and clears it. With the app
    /// already open, focus would otherwise stay wherever it was, the sidebar say, where Menu leaves the app.
    @Entry var focusesPlay: Binding<Bool> = .constant(false)
}

/// Play on a title a link opened: it takes focus as it appears (see `focusesPlay`).
private struct LinkFocus: ViewModifier {
    @Environment(\.focusesPlay) private var takesFocus
    @FocusState private var focused: Bool

    func body(content: Content) -> some View {
        content
            .focused($focused)
            .task {
                guard takesFocus.wrappedValue else { return }
                takesFocus.wrappedValue = false
                // Shown at once from the link's preview, it can appear while the screen is still sliding in, too
                // early to take focus: keep asking for a moment.
                for _ in 0..<8 where !focused {
                    focused = true
                    try? await Task.sleep(for: .milliseconds(150))
                }
            }
    }
}

extension View {
    func takesFocusFromLink() -> some View { modifier(LinkFocus()) }
}
