import FlexarrKit
import SwiftUI

/// Which of a library's two views its tab opens on: Browse (a hero and rows of just this library) or All (every
/// title, with sort, genre and A–Z). Settings › Libraries, for every library on this Apple TV.
enum LibraryOpening: String, CaseIterable {
    case browse
    case all

    static let storageKey = "FlexarrLibraryOpensOn"

    var label: String {
        switch self {
        case .browse: "Browse"
        case .all: "All titles"
        }
    }
}

/// A film or TV library's tab: Browse or All, switched with the pills at the top. A row's "See all" opens All on top
/// instead, so Back comes back to the rows.
struct LibraryTab: View {
    let library: Library
    @AppStorage(LibraryOpening.storageKey) private var opensOn = LibraryOpening.browse
    /// Picked with the switch; until then (and again after Settings changes), the setting.
    @State private var picked: LibraryOpening?

    var body: some View {
        let view = Binding { picked ?? opensOn } set: { picked = $0 }
        // Once the switch has been used, the view that replaces this one puts focus back on it.
        Group {
            switch view.wrappedValue {
            case .browse: LibraryBrowseView(library: library, view: view)
            case .all: LibraryView(library: library, viewSwitch: view)
            }
        }
        .environment(\.focusesViewSwitch, picked != nil)
        .onChange(of: opensOn) { picked = nil }
    }
}

/// "Browse" and "All films" (or "All shows") as quiet text at the top-left of both views. Plain words rather than
/// pills, so it doesn't read as a second tab bar under the real one; the lit tab already names the library.
struct LibraryViewSwitch: View {
    let library: Library
    @Binding var view: LibraryOpening
    @Environment(\.focusesViewSwitch) private var takesFocus
    @FocusState private var focused: LibraryOpening?

    /// From the top of the screen: under the tab bar, which sits 46pt from the top and is 68pt tall.
    static let topInset: CGFloat = 150

    var body: some View {
        HStack(spacing: 6) {
            option(.browse, "Browse")
            option(.all, library.type == .tvShows ? "All shows" : "All films")
        }
        // Words line up with the edge below them; the padding is for the capsule focus puts behind them.
        .padding(.leading, -22)
        // Coming into the switch lands on the view shown.
        .defaultFocus($focused, view)
        .onAppear {
            // The view chosen replaces the one whose switch had focus, which would otherwise go to the first word.
            if takesFocus { focused = view }
        }
    }

    private func option(_ opening: LibraryOpening, _ title: String) -> some View {
        Button(title) { view = opening }
            .buttonStyle(ViewSwitchStyle(selected: view == opening))
            .focused($focused, equals: opening)
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
                    HomeHero(items: browse.featured, overviews: browse.overviews, index: $featuredIndex, top: AnyView(viewSwitch))
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
    /// The library view on screen came from its switch, so its switch takes focus as it appears.
    @Entry var focusesViewSwitch = false
}
