import FlexarrKit
@preconcurrency import TVServices

/// Fills the Apple TV Home screen's Top Shelf with the profile's Continue Watching: full-bleed artwork, a Play button,
/// and how long it runs. Selecting one opens it in Flexarr; Play resumes it.
final class ContentProvider: TVTopShelfContentProvider {
    override func loadTopShelfContent() async -> (any TVTopShelfContent)? {
        guard let signIn = SharedSignIn.load() else { return nil }
        let client = FlexarrClient(baseURL: signIn.address, apiKey: signIn.apiKey, profileToken: signIn.profileToken)
        guard let home = try? await client.home() else { return nil }
        let watching = home.rows.first(where: { $0.id == "continue" })?.items ?? []
        let (titles, context) = watching.isEmpty ? (home.featured, "Featured") : (watching, "Continue Watching")
        let items = titles.prefix(10).compactMap { item(for: $0, context: context, client: client) }
        return items.isEmpty ? nil : TVTopShelfCarouselContent(style: .details, items: items)
    }

    private func item(for summary: ItemSummary, context: String, client: FlexarrClient) -> TVTopShelfCarouselItem? {
        let artwork = summary.artwork.backdrop ?? summary.artwork.thumb
        guard let small = client.imageURL(artwork, width: 1920), let large = client.imageURL(artwork, width: 3840),
              let open = URL(string: "flexarr://item/\(summary.id)"), let play = URL(string: "flexarr://play/\(summary.id)") else { return nil }
        let item = TVTopShelfCarouselItem(identifier: summary.id)
        item.contextTitle = context
        if summary.kind == .episode {
            item.title = summary.seriesTitle ?? summary.title
            let number = [summary.seasonNumber.map { "S\($0)" }, summary.episodeNumber.map { "E\($0)" }].compactMap { $0 }.joined(separator: " ")
            item.summary = number.isEmpty ? summary.title : "\(number)  \(summary.title)"
        } else {
            item.title = summary.title
            item.summary = summary.year.map(String.init)
        }
        item.setImageURL(small, for: .screenScale1x)
        item.setImageURL(large, for: .screenScale2x)
        if let duration = summary.durationSeconds, duration > 0 { item.duration = duration }
        item.displayAction = TVTopShelfAction(url: open)
        item.playAction = TVTopShelfAction(url: play)
        return item
    }
}
