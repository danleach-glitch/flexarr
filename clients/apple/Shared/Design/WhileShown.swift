import SwiftUI

extension View {
    /// Runs `work` (a loop that asks the server every so often) only while this view is on screen and the app is in
    /// front: stopped when another screen is pushed over it or the app goes to the background (music can keep it alive
    /// there), and started again, from the top, when it's back.
    func whileShown(_ work: @escaping @MainActor () async -> Void) -> some View {
        modifier(WhileShown(work: work))
    }
}

private struct WhileShown: ViewModifier {
    @Environment(\.scenePhase) private var scenePhase
    @State private var isVisible = false
    let work: @MainActor () async -> Void

    func body(content: Content) -> some View {
        content
            .onAppear { isVisible = true }
            .onDisappear { isVisible = false }
            .task(id: isVisible && scenePhase == .active) {
                guard isVisible, scenePhase == .active else { return }
                await work()
            }
    }
}
