import FlexarrKit
import SwiftUI

struct SearchView: View {
    @Environment(AppModel.self) private var model
    @Environment(Router.self) private var router
    @State private var query = ""
    @State private var results: SearchResults?
    @State private var error: String?

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 30) {
                if let results {
                    if results.isEmpty {
                        StatusView(state: .empty("Nothing matches \u{201C}\(query)\u{201D}."))
                            .frame(height: 300)
                    }
                    if !results.titles.isEmpty {
                        section("Movies and shows") {
                            ForEach(results.titles) { item in
                                PosterCard(item: item) { router.push(.item(item)) }
                            }
                        }
                    }
                    if !results.episodes.isEmpty {
                        section("Episodes") {
                            ForEach(results.episodes) { item in
                                WideCard(item: item, width: 440) { router.push(.item(item)) }
                            }
                        }
                    }
                    if !results.people.isEmpty {
                        section("People") {
                            ForEach(results.people) { person in
                                PersonCard(name: person.name, detail: person.credits == 1 ? "1 title" : "\(person.credits) titles", image: person.image) {
                                    router.push(.person(id: person.id, name: person.name))
                                }
                            }
                        }
                    }
                } else if let error {
                    StatusView(state: .failed(error) { Task { await search() } })
                        .frame(height: 300)
                }
            }
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .ignoresSafeArea(edges: .horizontal)
        .background(RoomBackground(ambient: nil))
        .searchable(text: $query, prompt: "Movies, shows, episodes and people")
        .onAppear(perform: takeSiriSearch)
        .onChange(of: model.pendingSearch) { takeSiriSearch() }
        .task(id: query) {
            // Debounce typing so each keystroke doesn't hit the server.
            try? await Task.sleep(for: .milliseconds(300))
            guard !Task.isCancelled else { return }
            await search()
        }
    }

    private func section(_ title: String, @ViewBuilder content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            RowHeading(title: title)
            ScrollView(.horizontal) {
                LazyHStack(alignment: .top, spacing: Layout.rowSpacing) { content() }
                    .padding(.horizontal, Layout.edge)
                    .padding(.top, 28)
                    .padding(.bottom, 40)
            }
            .scrollClipDisabled()
            .focusSection()
        }
    }

    /// "Search for Fargo in Flexarr" said to Siri: the words go in the field and the search runs.
    private func takeSiriSearch() {
        guard let words = model.pendingSearch else { return }
        model.pendingSearch = nil
        query = words
    }

    private func search() async {
        let text = query.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty, let client = model.client else {
            results = nil
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
    @Environment(Router.self) private var router
    let id: String
    let name: String
    @State private var person: Person?
    @State private var error: String?

    private let columns = Array(repeating: GridItem(.fixed(Layout.posterWidth), spacing: Layout.gridSpacing, alignment: .top), count: 6)

    var body: some View {
        ScrollView(.vertical) {
            VStack(alignment: .leading, spacing: 40) {
                HStack(spacing: 40) {
                    RemoteImage(url: model.client?.imageURL(person?.image, width: 300)) { Color.seat }
                        .frame(width: 170, height: 170)
                        .clipShape(Circle())
                    VStack(alignment: .leading, spacing: 6) {
                        Text(name).font(.marquee(72, weight: .heavy)).foregroundStyle(Color.screen)
                        if let person {
                            Text(person.credits.count == 1 ? "1 title in your library" : "\(person.credits.count) titles in your library")
                                .font(.system(size: 26)).foregroundStyle(Color.usher)
                        }
                    }
                }
                if let person {
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 64) {
                        ForEach(person.credits, id: \.item.id) { credit in
                            PosterCard(item: credit.item) { router.push(.item(credit.item)) }
                        }
                    }
                } else if let error {
                    StatusView(state: .failed(error) { Task { await load() } }).frame(height: 400)
                } else {
                    ProgressView().frame(maxWidth: .infinity).padding(60)
                }
            }
            .padding(.horizontal, Layout.edge)
            .padding(.bottom, 80)
        }
        .scrollClipDisabled()
        .background(RoomBackground(ambient: person?.credits.first?.item.glowColor, intensity: 0.22))
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
