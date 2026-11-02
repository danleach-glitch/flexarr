import SwiftUI

/// An audiobook genre's tile picture, as on the web: books have no backdrops, so the genre's newest covers stand fanned
/// in the top right, over the front one blurred and darkened to fill the tile with its colours. Fills the frame it's
/// given; with no covers (servers before 0.58), the quiet wash a film genre without a backdrop has.
struct BookGenreArt: View {
    @Environment(AppModel.self) private var model
    let covers: [String]
    /// The fan's height, as a share of the tile's.
    var fan: CGFloat = 0.5

    var body: some View {
        GeometryReader { proxy in
            let shown = Array(covers.prefix(3))
            let side = proxy.size.height * fan
            ZStack(alignment: .topTrailing) {
                if let front = shown.first {
                    RemoteImage(url: model.client?.imageURL(front, width: 185)) { wash }
                        .frame(width: proxy.size.width * 1.4, height: proxy.size.height * 1.4)
                        .blur(radius: 26)
                        .saturation(1.5)
                        .brightness(-0.25)
                        .frame(width: proxy.size.width, height: proxy.size.height)
                        .clipped()
                    ZStack(alignment: .trailing) {
                        // Back to front: each further back is smaller, dimmer and turned further out to the left.
                        ForEach(Array(shown.enumerated()).reversed(), id: \.offset) { index, cover in
                            RemoteImage(url: model.client?.imageURL(cover, width: 342))
                                .frame(width: side, height: side)
                                .overlay(Color.black.opacity([0, 0.3, 0.5][index]))
                                .clipShape(RoundedRectangle(cornerRadius: Radius.art, style: .continuous))
                                .shadow(color: .black.opacity(0.45), radius: 12, y: 5)
                                .scaleEffect([1, 0.9, 0.8][index])
                                .rotationEffect(.degrees([0, -7, -13][index]))
                                .offset(x: -side * [0, 0.34, 0.62][index])
                        }
                    }
                    .frame(width: side, height: side)
                    .padding(.top, proxy.size.height * 0.09)
                    .padding(.trailing, proxy.size.width * 0.06)
                } else {
                    wash
                }
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
        }
        .accessibilityHidden(true)
    }

    private var wash: some View {
        LinearGradient(colors: [Color.silver.opacity(0.3), Color.seat], startPoint: .topLeading, endPoint: .bottomTrailing)
    }
}
