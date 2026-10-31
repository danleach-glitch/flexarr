import FlexarrKit
import SwiftUI

/// First run. Servers on the network are listed (they announce themselves): pick one and approve the code it
/// shows, here in Safari or on any device signed in to Flexarr. Typing an address (and API key) is still there
/// for servers that can't be found, such as over Tailscale.
struct ConnectView: View {
    @Environment(AppModel.self) private var model
    @State private var browser = ServerBrowser()
    @State private var step: Step = .choose

    private enum Step: Equatable {
        case choose
        case pair(DiscoveredServer)
        case manual
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 28) {
                VStack(alignment: .leading, spacing: 10) {
                    HStack(spacing: 12) {
                        BrandMark(size: 56)
                        Text("flexarr")
                            .font(.marquee(64, weight: .black))
                            .foregroundStyle(Color.screen)
                    }
                    Text("Your entertainment, on every screen.")
                        .font(.title3.weight(.medium))
                        .foregroundStyle(Color.usher)
                    if let notice = model.signedOutNotice {
                        Text(notice)
                            .font(.body.weight(.semibold))
                            .foregroundStyle(Color.screen)
                            .padding(.top, 8)
                    }
                }

                switch step {
                case .choose:
                    ChooseServer(browser: browser, onPick: { step = .pair($0) }, onManual: { step = .manual })
                case .pair(let server):
                    PairWithServer(server: server, onBack: { step = .choose })
                case .manual:
                    ManualConnect(onPair: { step = .pair($0) }, onBack: { step = .choose })
                }
            }
            .padding(24)
            .padding(.top, 40)
            .frame(maxWidth: 520, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .scrollDismissesKeyboard(.interactively)
        .background(RoomBackground(ambient: .silver, intensity: 0.22))
        .animation(.easeInOut(duration: 0.2), value: step)
        .onAppear {
            browser.start()
            // Signed out on the server: show a code for the same server straight away.
            if model.signedOutNotice != nil, let server = model.store.lastAddress.flatMap(FlexarrClient.normalizeAddress) {
                step = .pair(DiscoveredServer(name: server.host() ?? "Flexarr", address: server))
            }
            #if DEBUG
            // `-FlexarrPairWith <url>` opens the code screen directly (screenshots and UI tests).
            if let server = UserDefaults.standard.string(forKey: "FlexarrPairWith").flatMap(FlexarrClient.normalizeAddress) {
                step = .pair(DiscoveredServer(name: server.host() ?? "Flexarr", address: server))
            }
            #endif
        }
        .onDisappear { browser.stop() }
    }
}

// MARK: - Servers on the network

private struct ChooseServer: View {
    let browser: ServerBrowser
    let onPick: (DiscoveredServer) -> Void
    let onManual: () -> Void
    /// Nothing has turned up for a while: say what to check.
    @State private var lookingAWhile = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Servers on your network")
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(Color.usher)
            if browser.servers.isEmpty {
                HStack(spacing: 12) {
                    ProgressView()
                    Text("Looking for Flexarr…").foregroundStyle(Color.usher)
                }
                .padding(.vertical, 8)
                if lookingAWhile {
                    Text("Can’t see your server? Check it’s running and this \(ThisDevice.name) is on the same Wi-Fi, or enter its address.")
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(Color.screen)
                }
                NewToFlexarr()
                    .padding(.top, 8)
            } else {
                ForEach(browser.servers) { server in
                    Button { onPick(server) } label: {
                        HStack(spacing: 12) {
                            Image(systemName: "server.rack").foregroundStyle(Color.silver)
                            Text(server.name).font(.body.weight(.semibold)).foregroundStyle(Color.screen)
                            Spacer()
                            Image(systemName: "chevron.right").font(.footnote.weight(.semibold)).foregroundStyle(Color.usher)
                        }
                        .padding(16)
                        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color.seat))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(CardButtonStyle())
                }
            }
            Button("Enter an address instead", action: onManual)
                .font(.body.weight(.semibold))
                .padding(.top, 6)
        }
        .task {
            guard (try? await Task.sleep(for: .seconds(6))) != nil else { return }
            lookingAWhile = true
        }
    }
}

/// Shown while nothing is found: the app is nothing without the server, and App Store downloads often don't
/// know there is one.
private struct NewToFlexarr: View {
    @Environment(\.openURL) private var openURL

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .top, spacing: 14) {
                Image(systemName: "server.rack")
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(Color.silver)
                    .frame(width: 44, height: 44)
                    .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.silver.opacity(0.16)))
                VStack(alignment: .leading, spacing: 4) {
                    Text("New to Flexarr?")
                        .font(.headline)
                        .foregroundStyle(Color.screen)
                    Text("You’ll need the Flexarr server on a computer at home: a Mac, a NAS or anything with Docker.")
                        .font(.subheadline)
                        .foregroundStyle(Color.usher)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            HStack(spacing: 8) {
                badge("Completely free", systemImage: "checkmark")
                badge("Under a minute", systemImage: "clock")
            }
            Button { openURL(WelcomeView.installURL) } label: {
                Label("Set up a server", systemImage: "arrow.up.right")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(ActionButtonStyle(prominent: true))
        }
        .padding(18)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background {
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .fill(Color.seat)
                .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(Color.silver.opacity(0.22)))
        }
    }

    private func badge(_ title: LocalizedStringKey, systemImage: String) -> some View {
        Label(title, systemImage: systemImage)
            .font(.footnote.weight(.semibold))
            .foregroundStyle(Color.screen)
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
            .background(Capsule().fill(Color.screen.opacity(0.1)))
    }
}

// MARK: - Approve the code

private struct PairWithServer: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openURL) private var openURL
    let server: DiscoveredServer
    let onBack: () -> Void
    @State private var pairing: Pairing?
    /// Try again after "Don't allow": a fresh start, with a fresh code the phones are asked about.
    @State private var attempt = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            if let pairing, pairing.declined {
                VStack(alignment: .leading, spacing: 12) {
                    Text("Not allowed")
                        .font(.title2.weight(.bold))
                        .foregroundStyle(Color.screen)
                    Text("The main profile turned this \(ThisDevice.name) away. If that was a mistake, try again and allow it this time.")
                        .font(.body)
                        .foregroundStyle(Color.usher)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Button("Try again") { attempt += 1 }
                    .buttonStyle(ActionButtonStyle(prominent: true))
            } else if let pairing, let start = pairing.current {
                Text("Connect this \(ThisDevice.name)")
                    .font(.title2.weight(.bold))
                    .foregroundStyle(Color.screen)

                // Short steps for someone who's never done this: a phone signed in as the main profile is asked to
                // allow it, and the web app does it without one. A new server has nobody to allow it yet, so setting
                // it up in a browser comes first.
                if pairing.needsSetup {
                    VStack(alignment: .leading, spacing: 14) {
                        NumberedStep(1) {
                            Text("On your computer, open **\(server.address.absoluteString)** in a web browser.")
                        }
                        NumberedStep(2) {
                            Text(pairing.setupNeedsCode
                                ? "Type in the setup code from the server's log, then click **Set up Flexarr**."
                                : "Click **Set up Flexarr**.")
                        }
                        NumberedStep(3) { Text("Go to **Settings**, then **Security**, then **Connect a device**.") }
                        NumberedStep(4) { Text("Type in this code:") }
                    }
                    code(start, renewed: pairing.renewedAt != nil)
                } else {
                    VStack(alignment: .leading, spacing: 14) {
                        Text("On a phone signed in to Flexarr, tap **Allow** when it asks. Check it shows this code:")
                            .font(.body)
                            .foregroundStyle(Color.screen)
                            .fixedSize(horizontal: false, vertical: true)
                        code(start, renewed: pairing.renewedAt != nil)
                        Text("Or on a computer, open **\(server.address.appending(path: "settings/tv").absoluteString)** and type in the code.")
                            .font(.subheadline)
                            .foregroundStyle(Color.usher)
                            .fixedSize(horizontal: false, vertical: true)
                            .textSelection(.enabled)
                    }
                }

                HStack(spacing: 10) {
                    ProgressView()
                    Text("This screen moves on by itself once it's allowed.")
                        .font(.footnote)
                        .foregroundStyle(Color.usher)
                }
            } else if pairing?.error == nil {
                ProgressView()
            }
            if let error = pairing?.error {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(Color.screen)
            }
            HStack(spacing: 20) {
                Button("Back", action: onBack)
                // Only works if Safari on this phone is signed in already, so it stays out of the way.
                if let pairing, let start = pairing.current, !pairing.needsSetup {
                    Button("Approve in Safari instead") { openURL(pairing.link(for: start)) }
                }
            }
            .font(.subheadline.weight(.semibold))
        }
        .task(id: "\(server.address.absoluteString)#\(attempt)") {
            let pairing = Pairing(server: server)
            self.pairing = pairing
            await pairing.run(model: model)
        }
    }

    private func code(_ start: PairingStart, renewed: Bool) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            // Regular width, not the condensed marquee face: the web pages show codes in the same SF Pro Black.
            Text(start.code)
                .font(.system(size: 52, weight: .black))
                .kerning(2)
                .foregroundStyle(Color.screen)
                .textSelection(.enabled)
            if renewed {
                Text("A new code")
                    .font(.subheadline)
                    .foregroundStyle(Color.usher)
            }
        }
    }
}

/// One step of the connect instructions: a number in a circle, then what to do.
private struct NumberedStep<Content: View>: View {
    let number: Int
    @ViewBuilder let content: Content

    init(_ number: Int, @ViewBuilder content: () -> Content) {
        self.number = number
        self.content = content()
    }

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Text("\(number)")
                .font(.subheadline.weight(.bold))
                .foregroundStyle(Color.room)
                .frame(width: 26, height: 26)
                .background(Circle().fill(Color.silver))
            content
                .padding(.top, 2)
                .font(.body)
                .foregroundStyle(Color.screen)
                .fixedSize(horizontal: false, vertical: true)
                .textSelection(.enabled)
        }
    }
}

// MARK: - Typed address and key

/// A server that can't be found (over Tailscale, say): type its address and connect with a code like any other.
/// The API key is only for connecting from outside the home network and tailnet.
private struct ManualConnect: View {
    @Environment(AppModel.self) private var model
    let onPair: (DiscoveredServer) -> Void
    let onBack: () -> Void
    @State private var address = ""
    @State private var apiKey = ""
    @State private var usesKey = false
    @State private var isConnecting = false
    @State private var error: String?
    @FocusState private var focus: Field?

    private enum Field { case address, key }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            field("Server address") {
                TextField("http://192.168.1.20:8383", text: $address)
                    .textContentType(.URL)
                    .keyboardType(.URL)
                    .autocorrectionDisabled()
                    .textInputAutocapitalization(.never)
                    .focused($focus, equals: .address)
                    .submitLabel(usesKey ? .next : .go)
                    .onSubmit { if usesKey { focus = .key } else { connect() } }
            }
            if usesKey {
                field("API key") {
                    SecureField("Paste your API key", text: $apiKey)
                        .focused($focus, equals: .key)
                        .submitLabel(.go)
                        .onSubmit { connect() }
                }
            } else {
                Text("You’ll get a code to approve, like a server found on your Wi-Fi.")
                    .font(.footnote)
                    .foregroundStyle(Color.usher)
            }

            HStack(spacing: 12) {
                Button(action: connect) {
                    if isConnecting {
                        HStack(spacing: 8) {
                            ProgressView().tint(.room)
                            Text("Connecting")
                        }
                    } else {
                        Label("Connect", systemImage: "arrow.right.circle.fill")
                    }
                }
                .buttonStyle(ActionButtonStyle(prominent: true))
                .disabled(isConnecting || address.trimmingCharacters(in: .whitespaces).isEmpty)
                Button("Back", action: onBack)
                    .buttonStyle(ActionButtonStyle())
            }

            if let error {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(Color.screen)
                    .padding(14)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(RoundedRectangle(cornerRadius: 12).fill(Color(red: 0.42, green: 0.2, blue: 0.28).opacity(0.6)))
            }

            if !usesKey {
                Button("Connecting from outside your home? Use an API key") {
                    usesKey = true
                    focus = .key
                }
                .font(.footnote.weight(.semibold))
            }
        }
        .onAppear {
            if address.isEmpty { address = model.store.lastAddress ?? "" }
            focus = address.isEmpty || !usesKey ? .address : .key
        }
    }

    private func field(_ title: String, @ViewBuilder content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(Color.usher)
            content()
                .padding(12)
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.seat))
        }
    }

    private func connect() {
        guard !isConnecting else { return }
        if !usesKey {
            guard let server = FlexarrClient.normalizeAddress(address) else {
                error = "That doesn’t look like a server address. Try one like http://192.168.1.20:8383."
                return
            }
            onPair(DiscoveredServer(name: server.host() ?? "Flexarr", address: server))
            return
        }
        isConnecting = true
        error = nil
        Task {
            defer { isConnecting = false }
            do {
                try await model.connect(address: address, apiKey: apiKey)
            } catch {
                self.error = ConnectionError(error).message
            }
        }
    }
}
