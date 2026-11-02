import FlexarrKit
import SwiftUI

/// A film, TV or home videos library's own home page, as on the web and the TVs: a hero of its titles, then its rows
/// (Continue watching, the latest, Coming soon, its Top 10, its genres…). A row's See all opens the titles behind it.
struct LibraryBrowseView<Header: View>: View {
    @Environment(AppModel.self) private var model
    let library: Library
    /// Every row switched off (or none with anything in it) while the library has titles: show them all instead.
    var showAll: () -> Void = {}
    /// The Browse · All · Genres switch, at the top of the page.
    @ViewBuilder var header: Header

    @State private var browse: Home?
    @State private var error: String?
    @State private var featuredIndex = 0
    /// `model.refreshToken` when `browse` was last fetched.
    @State private var loadedToken: Int?
    @State private var loadedAt: Date?
    @State private var isVisible = false
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 12) {
                header
                content
            }
            .padding(.bottom, 32)
        }
        .background(RoomBackground(ambient: featured?.glowColor, intensity: 0.28).animation(.easeInOut(duration: 0.8), value: featured?.id))
        .refreshable { await load() }
        .task(id: model.refreshToken) {
            // Coming back from a detail screen runs this again: fetch only when something changed here, or what's
            // shown is old enough that something may have changed elsewhere.
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
        guard let items = browse?.featured, !items.isEmpty else { return nil }
        return items[featuredIndex % items.count]
    }

    @ViewBuilder
    private var content: some View {
        if let browse {
            let rows = browse.rows.filter { HomeRowSlot.shows($0, in: browse) }
            if browse.featured.isEmpty && rows.isEmpty {
                StatusView(state: .empty("Nothing in this library yet."))
                    .frame(height: 400)
            } else {
                LazyVStack(alignment: .leading, spacing: 28) {
                    if !browse.featured.isEmpty {
                        HomeHero(items: browse.featured, overviews: browse.overviews, index: $featuredIndex, resuming: browse.resuming)
                    }
                    ForEach(rows) { HomeRowSlot(row: $0, home: browse) }
                }
            }
        } else if let error {
            StatusView(state: .failed(error) { Task { await load() } })
                .frame(height: 400)
        } else {
            StatusView(state: .loading)
                .frame(height: 400)
        }
    }

    private func load() async {
        guard let client = model.client else { return }
        let token = model.refreshToken
        do {
            let loaded = try await client.libraryBrowse(library.id)
            if loaded.featured.isEmpty, !loaded.rows.contains(where: { HomeRowSlot.shows($0, in: loaded) }), library.itemCount > 0, library.scan == nil {
                showAll()
            }
            browse = loaded
            loadedToken = token
            loadedAt = Date()
            error = nil
        } catch {
            if browse == nil { self.error = ConnectionError(error).message }
        }
    }
}
