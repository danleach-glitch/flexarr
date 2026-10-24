import FlexarrKit
import SwiftUI

struct HomeView: View {
    @Environment(AppModel.self) private var model
    @State private var home: Home?
    @State private var error: String?
    @State private var featuredIndex = 0
    @State private var choosingProfile = false
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
        .toolbar {
            ToolbarItem(placement: .topBarLeading) {
                HStack(spacing: 8) {
                    BrandMark(size: 30)
                    Text("flexarr").font(.marquee(26, weight: .black)).foregroundStyle(Color.screen)
                }
                .accessibilityElement(children: .ignore)
                .accessibilityLabel("Flexarr")
            }
            if model.profiles.count > 1, let current = model.currentProfile {
                ToolbarItem(placement: .topBarTrailing) {
                    Button { choosingProfile = true } label: {
                        ProfileAvatar(profile: current, size: 32)
                    }
                    .accessibilityLabel("Profile: \(current.name)")
                }
            }
        }
        .toolbarBackground(.hidden, for: .navigationBar)
        .sheet(isPresented: $choosingProfile) {
            ProfilePickerView(marksCurrent: true)
                .presentationDetents([.medium, .large])
                .presentationBackground(Color.room)
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

    private var featured: ItemSummary? {
        guard let items = home?.featured, !items.isEmpty else { return nil }
        return items[featuredIndex % items.count]
    }

    private func content(_ home: Home) -> some View {
        ScrollView(.vertical) {
            LazyVStack(alignment: .leading, spacing: 28) {
                if !home.featured.isEmpty {
                    HomeHero(items: home.featured, overviews: home.overviews, index: $featuredIndex)
                }
                ForEach(home.rows) { row in
                    HomeRowView(row: row)
                }
            }
            .padding(.bottom, 32)
        }
        .ignoresSafeArea(edges: .top)
        .refreshable { await load() }
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

/// Featured titles to swipe through: backdrop, clear logo, facts, a line of overview, Play and More info.
struct HomeHero: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.verticalSizeClass) private var verticalSizeClass
    let items: [ItemSummary]
    let overviews: [String: String?]
    @Binding var index: Int

    /// Shorter on a phone held sideways, so the title and Play stay on screen.
    private var height: CGFloat { verticalSizeClass == .compact ? 340 : sizeClass == .regular ? 620 : 560 }

    var body: some View {
        TabView(selection: $index) {
            ForEach(Array(items.enumerated()), id: \.element.id) { offset, item in
                slide(item).tag(offset)
            }
        }
        .tabViewStyle(.page(indexDisplayMode: items.count > 1 ? .always : .never))
        .frame(height: height)
        .task(id: index) {
            // Moves on by itself after a while; a swipe restarts the clock.
            guard items.count > 1, (try? await Task.sleep(for: .seconds(10))) != nil else { return }
            withAnimation(.easeInOut(duration: 0.6)) { index = (index + 1) % items.count }
        }
    }

    private func slide(_ item: ItemSummary) -> some View {
        ZStack(alignment: .bottomLeading) {
            Color.clear
                .overlay {
                    RemoteImage(url: model.client?.imageURL(item.artwork.backdrop, width: sizeClass == .regular ? 1600 : 1000)) {
                        LinearGradient(colors: [item.glowColor.opacity(0.4), Color.room], startPoint: .top, endPoint: .bottom)
                    }
                }
                .clipped()
                .overlay {
                    LinearGradient(stops: [.init(color: .clear, location: 0.25), .init(color: .room.opacity(0.7), location: 1)],
                                   startPoint: .top, endPoint: .bottom)
                    LinearGradient(colors: [item.glowColor.opacity(0.3), .clear], startPoint: .bottomLeading, endPoint: .topTrailing)
                        .blendMode(.plusLighter)
                        .opacity(0.5)
                }
                // Fade out rather than painting the room colour, so there's no seam with the rows below.
                .mask(LinearGradient(stops: [.init(color: .black, location: 0.5), .init(color: .clear, location: 1)], startPoint: .top, endPoint: .bottom))

            VStack(alignment: .leading, spacing: 12) {
                TitleArt(item: item, maxWidth: 280, maxHeight: 110, fontSize: 52)
                FactsLine(facts: item.facts)
                if let overview = overviews[item.id] ?? nil {
                    Text(overview)
                        .font(.subheadline)
                        .foregroundStyle(Color.screen.opacity(0.85))
                        .lineLimit(2)
                        .frame(maxWidth: 520, alignment: .leading)
                }
                HStack(spacing: 10) {
                    Button { Task { await model.playBest(item, router: router) } } label: {
                        Label(item.isResumable ? "Resume" : "Play", systemImage: "play.fill")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: item.glowColor, prominent: true))
                    Button { router.push(.item(item)) } label: {
                        Label("More info", systemImage: "info.circle")
                    }
                    .buttonStyle(ActionButtonStyle(ambient: item.glowColor))
                }
                .padding(.top, 4)
            }
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 44)
        }
        .contentShape(Rectangle())
        .onTapGesture { router.push(.item(item)) }
    }
}

struct HomeRowView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    let row: HomeRow

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: row.title, action: linkedLibrary.map { library in { router.push(.library(library.0, library.1)) } })
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: row.style == .top10 ? 6 : Layout.rowSpacing) {
                    ForEach(Array(row.items.enumerated()), id: \.element.id) { index, item in
                        switch row.style {
                        case .wide:
                            WideCard(item: item, width: Layout.wideWidth(sizeClass))
                        case .top10:
                            Top10Card(rank: index + 1, item: item, width: Layout.posterWidth(sizeClass))
                        default:
                            PosterCard(item: item, width: Layout.posterWidth(sizeClass))
                        }
                    }
                }
                .padding(.horizontal, Layout.edge)
            }
            .scrollIndicators(.hidden)
        }
    }

    /// Rows link to "/library/{id}?sort=added"; map that to a library destination.
    private var linkedLibrary: (Library, LibrarySort)? {
        guard let link = row.link, let components = URLComponents(string: link),
              components.path.hasPrefix("/library/") else { return nil }
        let id = String(components.path.dropFirst("/library/".count))
        guard let library = model.libraries.first(where: { $0.id == id }) else { return nil }
        let sort = components.queryItems?.first(where: { $0.name == "sort" })?.value.flatMap(LibrarySort.init(rawValue:)) ?? .title
        return (library, sort)
    }
}
