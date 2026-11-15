import FlexarrKit
import SwiftUI

/// A room-coloured fade under the status bar and the back button, for a page whose hero runs up under them (Home, a
/// title's page). It comes in once the hero has scrolled away, so what follows doesn't run under the clock or sit behind
/// the back button. Laid out in the safe area (a layer of the page's ZStack, not the scroll view that ignores it), it
/// reaches up over the bars from there. The Mac has no status bar, and its toolbar has a background of its own.
struct TopBarFade: View {
    /// 0 to 1, from `trackingTopFade`.
    var strength: Double

    var body: some View {
        #if os(iOS)
        GeometryReader { proxy in
            let inset = proxy.safeAreaInsets.top
            LinearGradient(stops: [
                .init(color: .room, location: 0),
                .init(color: .room.opacity(0.92), location: 0.6),
                .init(color: .room.opacity(0), location: 1),
            ], startPoint: .top, endPoint: .bottom)
            .frame(height: inset + 28)
            .offset(y: -inset)
        }
        .opacity(strength)
        .animation(.easeOut(duration: 0.15), value: strength)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
        #endif
    }
}

extension View {
    /// Keeps `strength` (for `TopBarFade`) in step with how far this scroll view has scrolled past a hero `heroHeight`
    /// tall (0 for none). It changes in tenths, so scrolling redraws the page only now and then.
    func trackingTopFade(heroHeight: CGFloat, into strength: Binding<Double>) -> some View {
        onScrollGeometryChange(for: Double.self) { geometry in
            // Bars of about 100 points (status bar and back button, or an iPad's tab bar): close enough to time it by.
            HeroLayout.topFade(scrolled: geometry.contentOffset.y + geometry.contentInsets.top, heroHeight: heroHeight, topInset: 100)
        } action: { _, new in
            strength.wrappedValue = new
        }
    }
}
