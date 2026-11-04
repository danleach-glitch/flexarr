import FlexarrKit
import SwiftUI

/// First launch on the Mac: one page, the window's width, rather than the iPhone's three cards to swipe. What Flexarr
/// is, then its two steps side by side (a free server at home, then connecting to it), then on to the connect screen.
/// The same words as the iPhone's (WelcomeView), with the Mac's way of answering the network question.
struct MacWelcomeView: View {
    let onDone: () -> Void
    @Environment(\.openURL) private var openURL

    var body: some View {
        ScrollView {
            VStack(spacing: 40) {
                VStack(spacing: 18) {
                    BrandMark(size: 72)
                    Text(t("Your entertainment, on every screen."))
                        .font(.marquee(56, weight: .black))
                        .foregroundStyle(Color.screen)
                        .multilineTextAlignment(.center)
                    Text(t("Flexarr plays the movies, shows and music you keep at home, on this {device}, wherever you are.", ["device": ThisDevice.name]))
                        .font(.title3.weight(.medium))
                        .foregroundStyle(Color.usher)
                        .multilineTextAlignment(.center)
                        .frame(maxWidth: 560)
                }

                HStack(alignment: .top, spacing: 20) {
                    step(1, symbol: "server.rack", title: t("Set up your free server"),
                         message: t("The app plays from your own Flexarr server, on a computer in your home: a Mac, a NAS or anything with Docker. The server is completely free, and setting it up takes less than a minute.")) {
                        Button { openURL(WelcomeView.installURL) } label: {
                            Label(t("Set up at flexarr.io"), systemImage: "arrow.up.right")
                        }
                        .buttonStyle(ActionButtonStyle())
                    }
                    step(2, symbol: "wifi", title: t("Then connect"),
                         message: t("On the same network, this Mac finds your server by itself. If it asks to find devices on your network, click Allow. Then approve the code it shows and you’re in.")) {
                        EmptyView()
                    }
                }
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: 860)

                Button(action: onDone) {
                    Text(t("Find my server")).frame(minWidth: 220)
                }
                .buttonStyle(ActionButtonStyle(prominent: true))
                .keyboardShortcut(.defaultAction)
            }
            .padding(.horizontal, 48)
            .padding(.vertical, 56)
            .frame(maxWidth: .infinity)
            .containerRelativeFrame(.vertical, alignment: .center)
        }
        .background(RoomBackground(ambient: .silver, intensity: 0.22).ignoresSafeArea())
    }

    /// One step as a panel: its number and picture, what to do, and anything to do it with.
    private func step(_ number: Int, symbol: String, title: String, message: String,
                      @ViewBuilder action: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 12) {
                Image(systemName: symbol)
                    .font(.system(size: 20, weight: .semibold))
                    .foregroundStyle(Color.silver)
                    .frame(width: 44, height: 44)
                    .background(RoundedRectangle(cornerRadius: Radius.control, style: .continuous).fill(Color.silver.opacity(0.16)))
                Text(verbatim: "\(number)")
                    .font(.system(size: 13, weight: .bold))
                    .foregroundStyle(Color.usher)
                    .accessibilityHidden(true)
            }
            Text(title)
                .font(.marquee(26, weight: .bold))
                .foregroundStyle(Color.screen)
            Text(message)
                .font(.body)
                .foregroundStyle(Color.usher)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            action()
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background {
            RoundedRectangle(cornerRadius: Radius.panel, style: .continuous)
                .fill(Color.seat)
                .overlay(RoundedRectangle(cornerRadius: Radius.panel, style: .continuous).strokeBorder(Color.screen.opacity(0.06)))
        }
    }
}
