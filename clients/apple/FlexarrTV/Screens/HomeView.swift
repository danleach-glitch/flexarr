import FlexarrKit
import SwiftUI

struct HomeView: View {
    @Environment(AppModel.self) private var model
    @State private var home: Home?
    @State private var error: String?
    @State private var featuredIndex = 0
    /// `model.refreshToken` when `home` was last fetched.
    @State private var loadedToken: Int?
    @State private var loadedAt: Date?
    @State private var isVisible = false
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        ZStack {
            RoomBackground(ambient: featured?.glowColor, intensity: 0.28)
                .animation(.easeInOut(duration: 0.8), value: featured?.id)
            if let home {
                content(home)
            } else if let error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else {
                StatusView(state: .loading)
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
            // Back to the app after a while (tvOS keeps it running for days).
            if phase == .active, isVisible, Freshness.isStale(loadedAt) { Task { await load() } }
        }
    }

    private var featured: ItemSummary? {
        guard let items = home?.featured, !items.isEmpty else { return nil }
        return items[featuredIndex % items.count]
    }

    private func content(_ home: Home) -> some View {
        ScrollView(.vertical) {
            LazyVStack(alignment: .leading, spacing: 30) {
                if !home.featured.isEmpty {
                    HomeHero(items: home.featured, overviews: home.overviews, index: $featuredIndex)
                }
                ForEach(home.rows) { row in
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
            let loaded = try await client.home()
            home = loaded
            loadedToken = token
            loadedAt = Date()
            error = nil
        } catch {
            if home == nil { self.error = ConnectionError(error).message }
        }
    }
}

/// Full-bleed featured hero: backdrop, clear logo, facts, one-line overview, Play and More info.
/// Left on Play or right on More info moves between featured titles; it also advances on its own.
struct HomeHero: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let items: [ItemSummary]
    let overviews: [String: String?]
    @Binding var index: Int
    @FocusState private var focusedButton: HeroButton?
    @Namespace private var heroScope

    private enum HeroButton { case play, info }

    private var item: ItemSummary { items[index % items.count] }

    var body: some View {
        ZStack(alignment: .bottomLeading) {
            backdrop
                .id(item.id)
                .transition(.opacity)

            VStack(alignment: .leading, spacing: 26) {
                VStack(alignment: .leading, spacing: 24) {
                    TitleArt(item: item, maxWidth: 760, maxHeight: 230, fontSize: 110)
                    FactsLine(facts: item.facts)
                    if let overview = overviews[item.id] ?? nil {
                        Text(overview)
                            .font(.system(size: 27))
                            .foregroundStyle(Color.screen.opacity(0.85))
                            .lineLimit(2)
                            .lineSpacing(4)
                            .frame(maxWidth: 900, alignment: .leading)
                    }
                }
                .id(item.id)
                .transition(.opacity)

                HStack(spacing: 28) {
                    Button { play() } label: {
                        Label(item.isResumable ? "Resume" : "Play", systemImage: "play.fill")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: item.glowColor, prominent: true))
                    .focused($focusedButton, equals: .play)
                    .prefersDefaultFocus(true, in: heroScope)
                    .onMoveCommand { if $0 == .left { step(-1) } }

                    Button { router.push(.item(item)) } label: {
                        Label("More info", systemImage: "info.circle")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: item.glowColor))
                    .focused($focusedButton, equals: .info)
                    .onMoveCommand { if $0 == .right { step(1) } }
                }
                .padding(.top, 10)

                if items.count > 1 {
                    HStack(spacing: 12) {
                        ForEach(items.indices, id: \.self) { i in
                            Capsule()
                                .fill(i == index % items.count ? Color.screen : Color.screen.opacity(0.3))
                                .frame(width: i == index % items.count ? 34 : 12, height: 12)
                        }
                    }
                    .padding(.top, 6)
                }
            }
            .padding(.leading, Layout.edge)
            .padding(.bottom, 40)
            // The focusable part of the hero is this corner, which sits below the tab bar. Coming down from the tab bar
            // the focus engine looks for a section beneath it; one spanning the whole hero (tab bar included) isn't,
            // and the rows' second poster was winning.
            .focusSection()
        }
        .frame(height: 900)
        .frame(maxWidth: .infinity)
        // Coming down from the tab bar (or back up from the rows) lands on Play.
        .focusScope(heroScope)
        .defaultFocus($focusedButton, .play, priority: .userInitiated)
        .onChange(of: focusedButton) { old, new in
            // The focus engine picks the geometrically nearest button (often More info under
            // the tab bar); entering the hero should always land on Play.
            if old == nil, new == .info { focusedButton = .play }
        }
        .task(id: HeroClock(index: index, focused: focusedButton != nil)) {
            // While the hero has focus, crossfade to the next title after a while; a manual
            // step restarts the clock. Browsing the rows below leaves the room colour alone.
            guard focusedButton != nil else { return }
            try? await Task.sleep(for: .seconds(12))
            guard !Task.isCancelled else { return }
            step(1)
        }
    }

    private var backdrop: some View {
        RemoteImage(url: model.client?.imageURL(item.artwork.backdrop, width: 1920)) {
            Color.clear
        }
        .frame(width: 1920, height: 1080)
        .clipped()
        .overlay {
            // A scrim so text stays legible, then the ambient colour bleeding in from the corner.
            LinearGradient(stops: [.init(color: .room.opacity(0.92), location: 0), .init(color: .room.opacity(0.55), location: 0.4), .init(color: .clear, location: 0.75)],
                           startPoint: .leading, endPoint: .trailing)
            LinearGradient(colors: [item.glowColor.opacity(0.3), .clear], startPoint: .bottomLeading, endPoint: .topTrailing)
                .blendMode(.plusLighter)
                .opacity(0.6)
        }
        // Fade out rather than painting the room colour, so there is no seam with the rows below.
        .mask(LinearGradient(stops: [.init(color: .black, location: 0), .init(color: .black, location: 0.55), .init(color: .clear, location: 0.95)],
                             startPoint: .top, endPoint: .bottom))
        .frame(height: 900, alignment: .top)
    }

    private func step(_ delta: Int) {
        withAnimation(.easeInOut(duration: 0.6)) {
            index = (index + delta + items.count) % items.count
        }
    }

    private func play() {
        let item = item
        Task { await model.playBest(item, router: router) }
    }
}

private struct HeroClock: Hashable {
    let index: Int
    let focused: Bool
}

/// Clear logo when the title has one, otherwise the title set in marquee type.
struct TitleArt: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    var maxWidth: CGFloat
    var maxHeight: CGFloat
    var fontSize: CGFloat

    var body: some View {
        if let logo = model.client?.imageURL(item.artwork.logo, width: 800) {
            RemoteImage(url: logo, contentMode: .fit) {
                Color.clear
            }
            .frame(maxWidth: maxWidth, maxHeight: maxHeight, alignment: .bottomLeading)
            .shadow(color: .black.opacity(0.35), radius: 16)
            .accessibilityLabel(item.title)
        } else {
            title
        }
    }

    private var title: some View {
        Text(item.displayTitle)
            .font(.marquee(fontSize, weight: .black))
            .kerning(-1)
            .foregroundStyle(Color.screen)
            .lineLimit(2)
            .minimumScaleFactor(0.6)
            .frame(maxWidth: maxWidth * 1.3, alignment: .leading)
            .shadow(color: .black.opacity(0.35), radius: 16)
    }
}

struct HomeRowView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    let row: HomeRow

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            RowHeading(title: row.title)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: row.style == .top10 ? 30 : Layout.rowSpacing) {
                    ForEach(Array(row.items.enumerated()), id: \.element.id) { index, item in
                        switch row.style {
                        case .wide:
                            WideCard(item: item) { model.play(item) }
                        case .top10:
                            Top10Card(rank: index + 1, item: item) { router.push(.item(item)) }
                        default:
                            PosterCard(item: item) { router.push(.item(item)) }
                        }
                    }
                    if let library = linkedLibrary {
                        SeeAllCard(style: row.style) { router.push(.library(library.0, library.1)) }
                    }
                }
                .padding(.horizontal, Layout.edge)
                .padding(.top, 28)
                .padding(.bottom, 40)
            }
            .scrollClipDisabled()
            .focusSection()
        }
    }

    /// Rows link to "/library/{id}?sort=added"; map that to a library tab destination.
    private var linkedLibrary: (Library, LibrarySort)? {
        guard let link = row.link, let components = URLComponents(string: link),
              components.path.hasPrefix("/library/") else { return nil }
        let id = String(components.path.dropFirst("/library/".count))
        guard let library = model.libraries.first(where: { $0.id == id }) else { return nil }
        let sort = components.queryItems?.first(where: { $0.name == "sort" })?.value.flatMap(LibrarySort.init(rawValue:)) ?? .title
        return (library, sort)
    }
}

struct SeeAllCard: View {
    let style: HomeRowStyle
    let action: () -> Void

    var body: some View {
        let size = style == .wide ? CGSize(width: Layout.wideWidth, height: Layout.wideWidth * 9 / 16)
            : CGSize(width: Layout.posterWidth, height: Layout.posterWidth * 1.5)
        Button(action: action) {
            VStack(spacing: 16) {
                Image(systemName: "square.grid.2x2").font(.system(size: 48, weight: .semibold))
                Text("See all").font(.system(size: 28, weight: .semibold))
            }
            .foregroundStyle(Color.screen.opacity(0.85))
            .frame(width: size.width, height: size.height)
            .background(Color.seat)
        }
        .buttonStyle(.card)
    }
}
