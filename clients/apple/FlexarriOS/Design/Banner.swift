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
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        content.overlay(alignment: .top) {
            if let text {
                Banner(text: text, systemImage: systemImage)
                    .padding(.horizontal, Layout.edge)
                    .padding(.top, 8)
                    .transition(reduceMotion ? .opacity : .move(edge: .top).combined(with: .opacity))
                    .onTapGesture { dismiss() }
                    .task(id: text) {
                        UIAccessibility.post(notification: .announcement, argument: text)
                        // Long enough to read: three seconds, more for a longer notice.
                        try? await Task.sleep(for: .seconds(Banner.duration(text)))
                        if self.text == text { dismiss() }
                    }
            }
        }
        .animation(.easeOut(duration: 0.25), value: text)
    }

    private func dismiss() { text = nil }
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
