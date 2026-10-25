import FlexarrKit
import SwiftUI

struct SearchView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var query = ""
    @FocusState private var typing: Bool
    @State private var results: SearchResults?
    @State private var error: String?

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 24) {
                if let results {
                    if results.isEmpty {
                        StatusView(state: .empty("Nothing matches \u{201C}\(query)\u{201D}."))
                            .frame(height: 240)
                    }
                    if !results.titles.isEmpty {
                        section("Movies and shows") {
                            ForEach(results.titles) { item in
                                PosterCard(item: item, width: Layout.posterWidth(sizeClass))
                            }
                        }
                    }
                    if !results.episodes.isEmpty {
                        section("Episodes") {
                            ForEach(results.episodes) { item in
                                WideCard(item: item, width: Layout.wideWidth(sizeClass))
                            }
                        }
                    }
                    if !results.people.isEmpty {
                        section("People") {
                            ForEach(results.people) { person in
                                PersonCard(id: person.id, name: person.name,
                                           detail: person.credits == 1 ? "1 title" : "\(person.credits) titles", image: person.image)
                            }
                        }
                    }
                } else if let error {
                    StatusView(state: .failed(error) { Task { await search() } })
                        .frame(height: 240)
                } else {
                    StatusView(state: .empty("Find films, shows, episodes and people in your libraries."))
                        .frame(height: 240)
                }
            }
            .padding(.vertical, 12)
        }
        .scrollDismissesKeyboard(.immediately)
        .background(RoomBackground(ambient: nil))
        .navigationTitle("Search")
        // Always showing: left to itself, iOS tucks the field away until the page is pulled down.
        .searchable(text: $query, placement: .navigationBarDrawer(displayMode: .always), prompt: "Movies, shows, episodes and people")
        // Opening Search is for typing: the field's ready with the keyboard up.
        .searchFocused($typing)
        .onAppear { if query.isEmpty { typing = true } }
        .task(id: query) {
            // Debounce typing so each keystroke doesn't hit the server.
            try? await Task.sleep(for: .milliseconds(300))
            guard !Task.isCancelled else { return }
            await search()
        }
    }

    private func section(_ title: String, @ViewBuilder content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionHeading(title: title)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) { content() }
                    .padding(.horizontal, Layout.edge)
            }
            .scrollIndicators(.hidden)
        }
    }

    private func search() async {
        let text = query.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty, let client = model.client else {
            results = nil
            error = nil
            return
        }
        do {
            let found = try await client.search(text)
            guard text == query.trimmingCharacters(in: .whitespaces) else { return }
            results = found
            error = nil
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}

struct PersonView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.horizontalSizeClass) private var sizeClass
    let id: String
    let name: String
    @State private var person: Person?
    @State private var error: String?

    private var columns: [GridItem] {
        let minimum: CGFloat = sizeClass == .regular ? 140 : 100
        return [GridItem(.adaptive(minimum: minimum, maximum: minimum * 1.45), spacing: 12, alignment: .top)]
    }

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 24) {
                HStack(spacing: 16) {
                    Color.seat
                        .frame(width: 88, height: 88)
                        .overlay { RemoteImage(url: model.client?.imageURL(person?.image, width: 240)) { Color.seat } }
                        .clipShape(Circle())
                    VStack(alignment: .leading, spacing: 4) {
                        Text(name).font(.marquee(34, weight: .heavy)).foregroundStyle(Color.screen)
                        if let person {
                            Text(person.credits.count == 1 ? "1 title in your library" : "\(person.credits.count) titles in your library")
                                .font(.subheadline).foregroundStyle(Color.usher)
                        }
                    }
                }
                .padding(.horizontal, Layout.edge)
                if let person {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                        ForEach(person.credits, id: \.item.id) { credit in
                            PosterCard(item: credit.item)
                        }
                    }
                    .padding(.horizontal, Layout.edge)
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 300)
                } else {
                    ProgressView().frame(maxWidth: .infinity).padding(40)
                }
            }
            .padding(.vertical, 12)
        }
        .background(RoomBackground(ambient: person?.credits.first?.item.glowColor, intensity: 0.22))
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            person = try await client.person(id)
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}
