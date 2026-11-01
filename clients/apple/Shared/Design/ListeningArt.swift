import FlexarrKit
import SwiftUI

/// A book's or podcast episode's square art in a 16:9 card (Continue watching): the cover centred over a blurred,
/// darkened copy of itself, so it fills the card without cropping. Fills the frame it's given.
struct ListeningArt: View {
    @Environment(AppModel.self) private var model
    let item: ItemSummary
    /// The size the cover is fetched at.
    var imageWidth = 400
    /// Space around the cover, top and bottom.
    var inset: CGFloat = 10
    var cornerRadius: CGFloat = 4

    var body: some View {
        let url = model.client?.imageURL(path, width: imageWidth)
        GeometryReader { proxy in
            let side = max(0, proxy.size.height - inset * 2)
            ZStack {
                RemoteImage(url: url) {
                    LinearGradient(colors: [item.glowColor.opacity(0.45), Color.seat], startPoint: .topLeading, endPoint: .bottomTrailing)
                }
                .frame(width: proxy.size.width, height: proxy.size.height)
                .blur(radius: 24)
                .overlay(Color.black.opacity(0.4))
                .clipped()
                RemoteImage(url: url) {
                    ZStack {
                        Color.seat
                        Text(item.title)
                            .font(.marquee(side / 8, weight: .bold))
                            .foregroundStyle(Color.screen.opacity(0.8))
                            .multilineTextAlignment(.center)
                            .padding(side / 16)
                    }
                }
                .frame(width: side, height: side)
                .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
                .shadow(color: .black.opacity(0.35), radius: 8)
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
        }
        .accessibilityHidden(true)
    }

    /// An episode's own picture before its podcast's; a book's cover.
    private var path: String? {
        item.kind == .podcastEpisode ? (item.artwork.thumb ?? item.artwork.poster) : (item.artwork.poster ?? item.artwork.thumb)
    }
}
