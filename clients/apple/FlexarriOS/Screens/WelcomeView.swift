import SwiftUI

/// First launch on a fresh install: three short cards saying what Flexarr is, that it needs its server at home
/// (with the way to flexarr.io), and how the app finds it. People from the App Store often don't know there's a
/// server at all. Skippable; the connect screen repeats the link for anyone who skips.
struct WelcomeView: View {
    let onDone: () -> Void
    @Environment(\.openURL) private var openURL
    @State private var page = 0

    static let installURL = URL(string: "https://flexarr.io/#install")!

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Spacer()
                Button("Skip", action: onDone)
                    .font(.body.weight(.semibold))
                    .foregroundStyle(Color.usher)
                    .opacity(page < 2 ? 1 : 0)
            }
            .padding(.horizontal, 24)
            .frame(height: 44)

            TabView(selection: $page) {
                card(tag: 0) {
                    BrandMark(size: 96)
                } title: {
                    "Your entertainment, on every screen."
                } message: {
                    "Flexarr plays the movies, shows and music you keep at home, on this \(ThisDevice.name), wherever you are."
                }
                card(tag: 1) {
                    symbol("server.rack")
                } title: {
                    "Set up your free server"
                } message: {
                    "The app plays from your own Flexarr server, on a computer in your home: a Mac, a NAS or anything with Docker. The server is completely free, and setting it up takes less than a minute."
                } extra: {
                    Button { openURL(Self.installURL) } label: {
                        Label("Set up at flexarr.io", systemImage: "safari")
                    }
                    .buttonStyle(ActionButtonStyle())
                }
                card(tag: 2) {
                    symbol("wifi")
                } title: {
                    "Then connect"
                } message: {
                    "On the same Wi-Fi, this \(ThisDevice.name) finds your server by itself. When it asks to find devices on your network, tap Allow. Then approve the code it shows and you’re in."
                }
            }
            .tabViewStyle(.page(indexDisplayMode: .always))

            Button {
                if page < 2 { withAnimation { page += 1 } } else { onDone() }
            } label: {
                Text(page < 2 ? "Next" : "Find my server")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(ActionButtonStyle(prominent: true))
            .padding(.horizontal, 24)
            .padding(.bottom, 24)
            .frame(maxWidth: 520)
        }
        .background(RoomBackground(ambient: .silver, intensity: 0.22))
        #if DEBUG
        // `-FlexarrWelcomePage <n>` opens on a later card (screenshots).
        .onAppear { page = min(2, UserDefaults.standard.integer(forKey: "FlexarrWelcomePage")) }
        #endif
    }

    private func card(
        tag: Int,
        @ViewBuilder art: () -> some View,
        title: () -> LocalizedStringKey,
        message: () -> LocalizedStringKey,
        @ViewBuilder extra: () -> some View = { EmptyView() }
    ) -> some View {
        VStack(alignment: .leading, spacing: 18) {
            Spacer()
            art()
            Text(title())
                .font(.marquee(40, weight: .black))
                .foregroundStyle(Color.screen)
            Text(message())
                .font(.title3.weight(.medium))
                .foregroundStyle(Color.usher)
                .fixedSize(horizontal: false, vertical: true)
            extra()
                .padding(.top, 6)
            Spacer()
            Spacer()
        }
        .padding(.horizontal, 24)
        .frame(maxWidth: 520, alignment: .leading)
        .frame(maxWidth: .infinity)
        .tag(tag)
    }

    private func symbol(_ name: String) -> some View {
        Image(systemName: name)
            .font(.system(size: 56, weight: .semibold))
            .foregroundStyle(Color.silver)
            .frame(height: 96, alignment: .leading)
    }
}
