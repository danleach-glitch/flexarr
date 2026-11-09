import SwiftUI

/// A notice that only tells (something saved, sent or set): a small banner at the top that goes by itself, rather than
/// an alert to dismiss. VoiceOver reads it out. Questions and destructive confirmations stay alerts.
///
///     .banner($notice)
extension View {
    func banner(_ text: Binding<String?>, systemImage: String = "checkmark.circle.fill") -> some View {
        modifier(BannerModifier(text: text, systemImage: systemImage))
    }
}

private struct BannerModifier: ViewModifier {
    @Binding var text: String?
    let systemImage: String
    /// The notice being said, and what takes it away when its time is up.
    @State private var saying: (text: String, timeUp: Task<Void, Never>)?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        content.overlay(alignment: .top) {
            if let text {
                Banner(text: text, systemImage: systemImage)
                    .padding(.horizontal, Layout.edge)
                    .padding(.top, 8)
                    .transition(reduceMotion ? .opacity : .move(edge: .top).combined(with: .opacity))
                    .onTapGesture { dismiss() }
                    .task(id: text) { say(text) }
            }
        }
        .animation(.easeOut(duration: 0.25), value: text)
        // Taken away by the screen: said again if it comes up again.
        .onChange(of: text) { _, now in
            if let saying, saying.text != now { stopSaying() }
        }
    }

    /// Said once, and its time runs from then, whether or not the screen stays up. The task showing it doesn't own that:
    /// a screen being pushed starts it twice, which would cut the notice short, and coming back to the screen starts it
    /// again, which would say it again however long after.
    private func say(_ text: String) {
        guard saying?.text != text else { return }
        stopSaying()
        AccessibilityNotification.Announcement(text).post()
        let timeUp = Task {
            // Long enough to read: three seconds, more for a longer notice.
            guard (try? await Task.sleep(for: .seconds(Banner.duration(text)))) != nil else { return }
            saying = nil
            if self.text == text { self.text = nil }
        }
        saying = (text, timeUp)
    }

    private func stopSaying() {
        saying?.timeUp.cancel()
        saying = nil
    }

    private func dismiss() {
        stopSaying()
        text = nil
    }
}

struct Banner: View {
    let text: String
    var systemImage = "checkmark.circle.fill"

    var body: some View {
        Label(text, systemImage: systemImage)
            .font(.subheadline.weight(.semibold))
            .foregroundStyle(Color.screen)
            .multilineTextAlignment(.leading)
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .frame(maxWidth: 560, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: Radius.panel, style: .continuous).fill(Color.seatRaised))
            .overlay(RoundedRectangle(cornerRadius: Radius.panel, style: .continuous).strokeBorder(Color.aisle))
            .accessibilityAddTraits(.isStaticText)
    }

    static func duration(_ text: String) -> Double { min(8, 3 + Double(max(0, text.count - 40)) / 30) }
}
