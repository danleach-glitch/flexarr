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
    /// See `LiveGuideGrid.records`.
    var records = true

    @State private var kept: KeptLiveGuide?
    /// Why the guide couldn't be had, with none to show.
    @State private var failure: String?
    /// The end of the window the guide shows, once it has been moved on from where it opens.
    @State private var showingUntil: Date?

    var body: some View {
        ZStack {
            Color.black.opacity(0.8).ignoresSafeArea()
            VStack(alignment: .leading, spacing: 20) {
                Text(t("Guide"))
                    .font(.marquee(56, weight: .black))
                    .foregroundStyle(Color.screen)
                if let failure {
                    StatusView(state: .failed(failure) {
                        self.failure = nil
                        Task { await load() }
                    })
                    // Nothing else here can take it, and Menu needs something focused to reach this screen.
                    .focusSection()
                    .padding(.trailing, Layout.edge)
                } else {
                    LiveGuideGrid(client: client, channels: channels, guide: kept?.guide, onWatch: onWatch,
                                  onRecordingsChanged: { Task { await load(force: true) } }, focusChannel: currentId, records: records,
                                  onWindow: { showingUntil = $0 })
                }
            }
            .padding(.leading, Layout.edge)
            .padding(.top, 50)
        }
        .onExitCommand(perform: onClose)
        .task { await load() }
        .onChange(of: showingUntil) { Task { await load() } }
    }

    /// Every channel's guide for the window shown and a margin (`LiveGuidePlan`); again after a recording was set or
    /// taken off (`force`, for its marks), or as the window moves towards its end.
    private func load(force: Bool = false) async {
        let opens = LiveGuideGrid.halfHour(before: Date()).addingTimeInterval(LiveGuideGrid.window)
        guard let request = LiveGuidePlan.next(kept: kept, scope: .all, shown: channels.map(\.id), showingUntil: max(showingUntil ?? opens, opens),
                                               keepAhead: LiveGuideGrid.keepAhead, askAhead: LiveGuideGrid.askAhead, force: force) else { return }
        do {
            kept = try await client.liveGuide(request, kept: kept)
            failure = nil
        } catch {
            // With a guide to show, it stays up, and the next move asks again.
            if kept == nil, !Task.isCancelled { failure = ConnectionError(error).message }
        }
    }

    /// The screen the player presents, over itself.
    static func controller(client: FlexarrClient, channels: [LiveChannel], currentId: String?, records: Bool,
                           onWatch: @escaping (LiveChannel) -> Void, onClose: @escaping () -> Void) -> UIViewController {
        let host = UIHostingController(rootView: LiveGuideOverlay(client: client, channels: channels, currentId: currentId, onWatch: onWatch,
                                                                  onClose: onClose, records: records))
        host.modalPresentationStyle = .overFullScreen
        host.view.backgroundColor = .clear
        return host
    }
}
