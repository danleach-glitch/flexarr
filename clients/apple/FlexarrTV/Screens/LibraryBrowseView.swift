import FlexarrKit
import SwiftUI

/// Which of a library's views its tab opens on: Browse (a hero and rows of just this library), All (every title, with
/// sort, genre and A–Z) or Genres (a tile for each). Settings › Libraries, for every library on this Apple TV.
enum LibraryOpening: String, CaseIterable {
    case browse
    case all
    case genres

    static let storageKey = "FlexarrLibraryOpensOn"

    var label: String {
        switch self {
        case .browse: "Browse"
        case .all: "All titles"
        case .genres: "Genres"
        }
    }

    /// Films and shows have genres; a home videos library opens on Browse instead.
    static func hasGenres(_ library: Library) -> Bool { library.type == .movies || library.type == .tvShows }
}

/// A film or TV library's tab: Browse or All, switched with the pills at the top. A row's "See all" opens All on top
/// instead, so Back comes back to the rows.
struct LibraryTab: View {
    let library: Library
    @AppStorage(LibraryOpening.storageKey) private var opensOn = LibraryOpening.browse
    /// Picked with the switch; until then (and again after Settings changes), the setting.
    @State private var picked: LibraryOpening?
    /// Just switched: the view taking over puts focus on its switch once, as the one pressed has gone. Coming back to
    /// the tab later leaves focus in the sidebar.
    @State private var switching = false
    /// Focus has just come to the switch with the tab (see `focusesContent`).
    @State private var arriving = false

    var body: some View {
        let view = Binding { picked ?? opensOn } set: {
            switching = $0 != (picked ?? opensOn)
            picked = $0
        }
        Group {
            switch view.wrappedValue {
            case .genres where LibraryOpening.hasGenres(library): LibraryGenresView(library: library, view: view)
            case .browse, .genres: LibraryBrowseView(library: library, view: view)
            case .all: LibraryView(library: library, viewSwitch: view)
            }
        }
        .environment(\.focusesViewSwitch, $switching)
        .environment(\.focusesContent, $arriving)
        .onChange(of: opensOn) { picked = nil }
    }
}

/// "Browse", "All films" (or "All shows") and "Genres" as quiet text at the top-left of both views. Plain words rather than
/// pills, so it doesn't read as a second row of tabs; the sidebar's label above already names the library.
struct LibraryViewSwitch: View {
    let library: Library
    @Binding var view: LibraryOpening
    /// Told when focus comes to the switch and leaves it.
    var focusChanged: (Bool) -> Void = { _ in }

    /// From the top of the screen: under the collapsed sidebar's label ("‹ Movies"), about where the tab bar was.
    static let topInset: CGFloat = 150

    var body: some View {
        let all = library.type == .tvShows ? "All shows" : library.type == .homeVideos ? "All folders" : "All films"
        ViewSwitch(choices: [(.browse, "Browse"), (.all, all)] + (LibraryOpening.hasGenres(library) ? [(.genres, "Genres")] : []),
                   selection: Binding { view == .genres && !LibraryOpening.hasGenres(library) ? .browse : view } set: { view = $0 },
                   focusChanged: focusChanged)
    }
}

/// Two or three views of a screen as quiet words at its top-left: a library's Browse and All, Discover's Browse and
/// Search.
struct ViewSwitch<Choice: Hashable>: View {
    let choices: [(Choice, String)]
    @Binding var selection: Choice
    /// Told when focus comes to the switch and leaves it.
    var focusChanged: (Bool) -> Void = { _ in }
    @Environment(\.focusesViewSwitch) private var takesFocus
    @Environment(\.focusesContent) private var passesFocus
    @FocusState private var focused: Choice?
    @State private var appearedAt = Date.distantPast

    var body: some View {
        HStack(spacing: 6) {
            ForEach(choices.indices, id: \.self) { index in
                option(choices[index].0, choices[index].1)
            }
        }
        // Words line up with the edge below them; the padding is for the capsule focus puts behind them.
        .padding(.leading, -22)
        // Coming into the switch lands on the view shown.
        .defaultFocus($focused, selection)
        .onChange(of: focused) { old, now in
            focusChanged(now != nil)
            // Selecting the library in the sidebar gives focus to whatever is at the top-left, which is this switch;
            // pass it on to the screen's first title. Up from there, or a switch of view, still comes here.
            if old == nil, now != nil, Date.now.timeIntervalSince(appearedAt) < 1.5 { passesFocus.wrappedValue = true }
            if now == nil { passesFocus.wrappedValue = false }
        }
        .onAppear {
            // The view chosen replaces the one whose switch had focus, which would otherwise go to the first word.
            guard takesFocus.wrappedValue else { appearedAt = .now; return }
            takesFocus.wrappedValue = false
            focused = selection
        }
    }

    private func option(_ choice: Choice, _ title: String) -> some View {
        Button(title) { selection = choice }
            .buttonStyle(ViewSwitchStyle(selected: selection == choice))
            .focused($focused, equals: choice)
    }
}

/// A word that's bright and underlined when it's the view shown, dim otherwise; focus lifts it onto a capsule.
private struct ViewSwitchStyle: ButtonStyle {
    var selected: Bool

    func makeBody(configuration: Configuration) -> some View {
        SwitchBody(configuration: configuration, selected: selected)
    }

    private struct SwitchBody: View {
        let configuration: Configuration
        let selected: Bool
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .font(.system(size: 28, weight: selected || isFocused ? .bold : .medium))
                .foregroundStyle(isFocused ? Color.room : (selected ? Color.screen : Color.screen.opacity(0.55)))
                .padding(.horizontal, 22)
                .frame(height: 56)
                .background {
                    Capsule().fill(Color.screen).opacity(isFocused ? 1 : 0)
                }
                .overlay(alignment: .bottom) {
                    if selected && !isFocused {
                        Capsule().fill(Color.screen).frame(height: 3).padding(.horizontal, 22).offset(y: -4)
                    }
                }
                // Over a bright backdrop the words need a shadow to stay legible; focused, the capsule needs one to lift.
                .shadow(color: .black.opacity(isFocused ? 0.3 : 0.45), radius: isFocused ? 16 : 10)
                .scaleEffect(isFocused ? 1.06 : 1)
                .animation(.spring(response: 0.3, dampingFraction: 0.75), value: isFocused)
        }
    }
}

/// A library's own home page, as on the web: a hero of its titles, then Continue watching, the latest, its Top 10,
/// the best not yet watched and a row for each of its biggest genres.
struct LibraryBrowseView: View {
    @Environment(AppModel.self) private var model
    let library: Library
    @Binding var view: LibraryOpening

    @State private var browse: Home?
    @State private var error: String?
    @State private var featuredIndex = 0
    /// `model.refreshToken` when `browse` was last fetched.
    @State private var loadedToken: Int?
    @State private var loadedAt: Date?
    @State private var isVisible = false
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        ZStack {
            RoomBackground(ambient: featured?.glowColor, intensity: 0.28)
                .animation(.easeInOut(duration: 0.8), value: featured?.id)
            if let browse, !browse.rows.isEmpty || !browse.featured.isEmpty {
                content(browse)
            } else {
                VStack(alignment: .leading, spacing: 0) {
                    viewSwitch
                        .padding(.leading, Layout.edge)
                        .padding(.top, LibraryViewSwitch.topInset)
                    Group {
                        if browse != nil {
                            StatusView(state: .empty(library.scan == nil ? "Nothing in this library yet." : "Titles appear here as the scan finds them."))
                        } else if let error {
                            StatusView(state: .failed(error) { Task { await load() } })
                        } else {
                            StatusView(state: .loading)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
                .ignoresSafeArea(edges: .top)
            }
        }
        .task(id: model.refreshToken) {
            // Coming back from a detail screen or another tab runs this again; fetch only when something changed
            // here, or what's shown is old enough that something may have changed elsewhere.
            guard loadedToken != model.refreshToken || Freshness.isStale(loadedAt) else { return }
            await load()
        }
        .onAppear { isVisible = true }
        .onDisappear { isVisible = false }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active, isVisible, Freshness.isStale(loadedAt) { Task { await load() } }
        }
    }

    private var viewSwitch: some View {
        LibraryViewSwitch(library: library, view: $view).focusSection()
    }

    private var featured: ItemSummary? {
        guard let items = browse?.featured, !items.isEmpty else { return nil }
        return items[featuredIndex % items.count]
    }

    private func content(_ browse: Home) -> some View {
        ScrollView(.vertical) {
            LazyVStack(alignment: .leading, spacing: 30) {
                if browse.featured.isEmpty {
                    viewSwitch
                        .padding(.leading, Layout.edge)
                        .padding(.top, LibraryViewSwitch.topInset)
                } else {
                    HomeHero(items: browse.featured, overviews: browse.overviews, index: $featuredIndex, resuming: browse.resuming, top: AnyView(viewSwitch))
                }
                ForEach(browse.rows.filter(\.hasContent)) { row in
                    HomeRowView(row: row)
                }
            }
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .ignoresSafeArea(edges: [.top, .horizontal])
    }

    private func load() async {
        guard let client = model.client else { return }
        let token = model.refreshToken
        do {
            let loaded = try await client.libraryBrowse(library.id)
            browse = loaded
            loadedToken = token
            loadedAt = Date()
            error = nil
        } catch {
            if browse == nil { self.error = ConnectionError(error).message }
        }
    }
}

extension EnvironmentValues {
    /// Set as the switch changes the view: the new view's switch takes focus as it appears, and clears it.
    @Entry var focusesViewSwitch: Binding<Bool> = .constant(false)
    /// Set as the switch is given focus with the tab: the hero's Play, or the first title, takes it (once there is
    /// one, while the switch still has focus), and clears it. Home sets it too as the app opens (see `MainTabView`).
    @Entry var focusesContent: Binding<Bool> = .constant(false)
}
