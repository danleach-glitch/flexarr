import FlexarrKit
import SwiftUI

/// Discover on the Mac: what's trending and popular from Seerr, in rows of posters to open and request, as the web
/// and the Apple TV have it. Search finds any film or show beyond the library too (the iPad's search, in the sidebar).
struct MacDiscoverView: View {
    @Environment(AppModel.self) private var model
    @State private var rows: [ExternalRow] = []
    @State private var loaded = false
    @State private var error: String?

    var body: some View {
        Group {
            if !loaded, let error {
                StatusView(state: .failed(error) { Task { await load() } })
            } else if !loaded {
                StatusView(state: .loading)
            } else if rows.isEmpty {
                StatusView(state: .empty(t("Nothing to discover right now.")))
            } else {
                ScrollView(.vertical) {
                    LazyVStack(alignment: .leading, spacing: 32) {
                        ForEach(rows) { row in
                            VStack(alignment: .leading, spacing: 12) {
                                SectionHeading(title: row.title)
                                    .padding(.horizontal, Layout.edge)
                                ScrollView(.horizontal) {
                                    LazyHStack(alignment: .top, spacing: 16) {
                                        ForEach(row.items) { OutsideTitleCard(title: $0, width: 150) }
                                    }
                                    .padding(.horizontal, Layout.edge)
                                }
                                .scrollIndicators(.hidden)
                            }
                        }
                    }
                    .padding(.vertical, 24)
                }
            }
        }
        .background(RoomBackground(ambient: nil))
        .navigationTitle(t(Words.discover))
        // Again on coming back, so what was just requested says so.
        .task { await load() }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            rows = try await client.discoverRows().filter { !$0.items.isEmpty }
            error = nil
        } catch {
            if !loaded { self.error = ConnectionError(error).message }
            return
        }
        loaded = true
    }
}
