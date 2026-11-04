import SwiftUI

/// A moment of the Flexarr mark and name as the app opens, fading into the window behind it.
struct SplashView: View {
    var body: some View {
        ZStack {
            Color.room.ignoresSafeArea()
            HStack(spacing: 18) {
                BrandMark(size: 84)
                Text(verbatim: "flexarr")
                    .font(.marquee(84, weight: .black, scales: false))
                    .foregroundStyle(Color.screen)
            }
        }
        .accessibilityHidden(true)
    }
}
