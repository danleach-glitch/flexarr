@preconcurrency import TVServices
import FlexarrKit

/// Fills the Apple TV Home screen's Top Shelf with the profile's Continue Watching: a row of posters, or of wide
/// artwork with each title's logo on it (Settings › Home screen), each with how far through it is. Selecting one
/// opens it in Flexarr; Play resumes it. With nothing on the go, the Home screen's featured titles instead.
final class ContentProvider: TVTopShelfContentProvider {
    override func loadTopShelfContent() async -> (any TVTopShelfContent)? {
        guard let signIn = SharedSignIn.load() else { return nil }
        let client = FlexarrClient(baseURL: signIn.address, apiKey: signIn.apiKey, profileToken: signIn.profileToken)
        guard let home = try? await client.home() else { return nil }
        let watching = home.rows.first(where: { $0.id == "continue" })?.items ?? []
        let (titles, heading) = watching.isEmpty ? (home.featured, "Featured") : (watching, "Continue Watching")
        let style = TopShelfStyle.current
        let items = titles.prefix(12).compactMap { item(for: $0, style: style, client: client) }
        guard !items.isEmpty else { return nil }
        let section = TVTopShelfItemCollection(items: items)
        section.title = heading
        return TVTopShelfSectionedContent(sections: [section])
    }

    private func item(for summary: ItemSummary, style: TopShelfStyle, client: FlexarrClient) -> TVTopShelfSectionedItem? {
        guard let (shape, small, large) = artwork(for: summary, style: style, client: client),
              let open = URL(string: "flexarr://item/\(summary.id)"), let play = URL(string: "flexarr://play/\(summary.id)") else { return nil }
        let item = TVTopShelfSectionedItem(identifier: summary.id)
        item.imageShape = shape
        item.setImageURL(small, for: .screenScale1x)
        item.setImageURL(large, for: .screenScale2x)
        if summary.kind == .episode {
            let number = [summary.seasonNumber.map { "S\($0)" }, summary.episodeNumber.map { "E\($0)" }].compactMap { $0 }.joined(separator: " ")
            item.title = [summary.seriesTitle ?? summary.title, number].filter { !$0.isEmpty }.joined(separator: " · ")
        } else {
            item.title = summary.title
        }
        if let duration = summary.durationSeconds, duration > 0, summary.userData.positionSeconds > 0 {
            item.playbackProgress = min(1, summary.userData.positionSeconds / duration)
        }
        item.displayAction = TVTopShelfAction(url: open)
        item.playAction = TVTopShelfAction(url: play)
        return item
    }

    /// Posters say what each one is and fit more of them (an episode's poster is its show's). Wide: the show's or
    /// film's backdrop with its logo, drawn by the server; an episode's still if there's no backdrop, else its poster.
    private func artwork(for summary: ItemSummary, style: TopShelfStyle, client: FlexarrClient) -> (TVTopShelfSectionedItem.ImageShape, URL, URL)? {
        if style == .wide {
            if let small = client.wideArtworkURL(summary.artwork, width: 780), let large = client.wideArtworkURL(summary.artwork, width: 1280) {
                return (.hdtv, small, large)
            }
            if let small = client.imageURL(summary.artwork.thumb, width: 780), let large = client.imageURL(summary.artwork.thumb, width: 1280) {
                return (.hdtv, small, large)
            }
        }
        guard let small = client.imageURL(summary.artwork.poster, width: 400), let large = client.imageURL(summary.artwork.poster, width: 800) else { return nil }
        return (.poster, small, large)
    }
}
