import FlexarrKit
import SwiftUI
import UIKit

/// The guide over the live picture, which carries on behind it: every channel (favourites first), opening on the one
/// being watched. Choosing what's on elsewhere changes channel; Menu goes back to the picture.
struct LiveGuideOverlay: View {
    let client: FlexarrClient
    let channels: [LiveChannel]
    let currentId: String?
    let onWatch: (LiveChannel) -> Void
    let onClose: () -> Void

    @State private var guide: LiveGuide?

    var body: some View {
        ZStack {
            Color.black.opacity(0.8).ignoresSafeArea()
            VStack(alignment: .leading, spacing: 20) {
                Text("Guide")
                    .font(.marquee(56, weight: .black))
                    .foregroundStyle(Color.screen)
                LiveGuideGrid(client: client, channels: channels, guide: guide, onWatch: onWatch,
                              onRecordingsChanged: { Task { await load() } }, focusChannel: currentId)
            }
            .padding(.leading, Layout.edge)
            .padding(.top, 50)
        }
        .onExitCommand(perform: onClose)
        .task { await load() }
    }

    private func load() async {
        if let loaded = try? await client.liveGuide(from: Date().addingTimeInterval(-3600), hours: 24) { guide = loaded }
    }

    /// The screen the player presents, over itself.
    static func controller(client: FlexarrClient, channels: [LiveChannel], currentId: String?,
                           onWatch: @escaping (LiveChannel) -> Void, onClose: @escaping () -> Void) -> UIViewController {
        let host = UIHostingController(rootView: LiveGuideOverlay(client: client, channels: channels, currentId: currentId, onWatch: onWatch, onClose: onClose))
        host.modalPresentationStyle = .overFullScreen
        host.view.backgroundColor = .clear
        return host
    }
}
