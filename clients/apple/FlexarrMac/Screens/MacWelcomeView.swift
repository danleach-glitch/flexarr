import FlexarrKit
import SwiftUI

/// First launch on the Mac: one page, the window's width, rather than the iPhone's three cards to swipe. What Flexarr
/// is, then its steps side by side (a free server at home, then connecting to it; or, with family invites, joining
/// someone else's), then on to the connect screen. The same words as the iPhone's (WelcomeView), with the Mac's way
/// of answering the network question.
struct MacWelcomeView: View {
    let onDone: () -> Void
    /// Straight to the code to give whoever invited this Mac.
    let onJoin: () -> Void
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
                    step(marker: "1", symbol: "server.rack", title: t("Set up your free server"),
                         message: t("The app plays from your own Flexarr server, on a computer in your home: a Mac, a NAS or anything with Docker. The server is completely free, and setting it up takes less than a minute.")) {
                        Button { openURL(WelcomeView.installURL) } label: {
                            Label(t("Set up at flexarr.io"), systemImage: "arrow.up.right")
                        }
                        .buttonStyle(ActionButtonStyle())
                    }
                    step(marker: "2", symbol: "wifi", title: t("Then connect"),
                         message: t("On the same network, this Mac finds your server by itself. If it asks to find devices on your network, click Allow. Then approve the code it shows and you’re in.")) {
                        Button(action: onDone) { Label(t("Find my server"), systemImage: "magnifyingglass") }
                            .buttonStyle(ActionButtonStyle(prominent: true))
                            .keyboardShortcut(.defaultAction)
                    }
                    if GuestInvite.guestInvitesOffered {
                        step(marker: t("Or"), symbol: "person.2.fill", title: t("Join a friend’s or family’s"),
                             message: t("Invited to someone else’s Flexarr? Give them the code this Mac shows and you’re in, with no server of your own.")) {
                            Button(action: onJoin) { Label(t("Join someone’s Flexarr"), systemImage: "key") }
                                .buttonStyle(ActionButtonStyle())
                        }
                    }
                }
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: GuestInvite.guestInvitesOffered ? 1080 : 760)
            }
            .padding(.horizontal, 48)
            .padding(.vertical, 56)
            .frame(maxWidth: .infinity)
            .containerRelativeFrame(.vertical, alignment: .center)
        }
        .background(RoomBackground(ambient: .silver, intensity: 0.22).ignoresSafeArea())
    }

    /// One step as a panel: its marker (a number, or Or for the other way in) and picture, what to do, and the
    /// button to do it with.
    private func step(marker: String, symbol: String, title: String, message: String,
                      @ViewBuilder action: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .center) {
                Text(marker)
                    .font(.system(size: marker.count > 1 ? 14 : 18, weight: .heavy))
                    .foregroundStyle(Color.room)
                    .frame(minWidth: 36, minHeight: 36)
                    .padding(.horizontal, marker.count > 1 ? 4 : 0)
                    .background(Capsule().fill(Color.silver))
                    .accessibilityLabel(marker)
                Spacer(minLength: 0)
                Image(systemName: symbol)
                    .font(.system(size: 22, weight: .semibold))
                    .foregroundStyle(Color.silver.opacity(0.8))
                    .accessibilityHidden(true)
            }
            Text(title)
                .font(.marquee(28, weight: .bold))
                .foregroundStyle(Color.screen)
                .fixedSize(horizontal: false, vertical: true)
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
        .accessibilityElement(children: .contain)
    }
}
