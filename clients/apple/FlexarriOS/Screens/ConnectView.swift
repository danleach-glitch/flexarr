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
                    Text("Your own cinema, wherever you are.")
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
                    ManualConnect(onBack: { step = .choose })
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
                    Text("Can’t see your server? Check this \(ThisDevice.name) is on the same Wi-Fi as it, or enter its address.")
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(Color.screen)
                }
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
            guard (try? await Task.sleep(for: .seconds(10))) != nil else { return }
            lookingAWhile = true
        }
    }
}

// MARK: - Approve the code

private struct PairWithServer: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openURL) private var openURL
    let server: DiscoveredServer
    let onBack: () -> Void
    @State private var pairing: Pairing?

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            if let pairing, let start = pairing.current {
                Text("Approve this \(ThisDevice.name)")
                    .font(.title2.weight(.bold))
                    .foregroundStyle(Color.screen)
                Text("Open the link in Safari where you’re signed in to Flexarr, and tap Allow. Or, on any device signed in to Flexarr, open Settings › Connect a TV and enter:")
                    .font(.subheadline)
                    .foregroundStyle(Color.usher)
                HStack(alignment: .firstTextBaseline, spacing: 12) {
                    Text(start.code)
                        .font(.marquee(56, weight: .black))
                        .kerning(3)
                        .foregroundStyle(Color.screen)
                        .textSelection(.enabled)
                    if pairing.renewedAt != nil {
                        Label("New code", systemImage: "arrow.clockwise")
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(Color.silver)
                    }
                }
                Button { openURL(pairing.link(for: start)) } label: {
                    Label("Approve in Safari", systemImage: "safari")
                }
                .buttonStyle(ActionButtonStyle(prominent: true))
                HStack(spacing: 10) {
                    ProgressView()
                    Text("Connecting to \(server.name). Waiting for approval…")
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
            Button("Back", action: onBack)
                .font(.body.weight(.semibold))
        }
        .task(id: server) {
            let pairing = Pairing(server: server)
            self.pairing = pairing
            await pairing.run(model: model)
        }
    }
}

// MARK: - Typed address and key

private struct ManualConnect: View {
    @Environment(AppModel.self) private var model
    let onBack: () -> Void
    @State private var address = ""
    @State private var apiKey = ""
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
                    .submitLabel(.next)
                    .onSubmit { focus = .key }
            }
            field("API key (not needed over Tailscale)") {
                SecureField("Paste your API key", text: $apiKey)
                    .focused($focus, equals: .key)
                    .submitLabel(.go)
                    .onSubmit { connect() }
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
        }
        .onAppear {
            if address.isEmpty { address = model.store.lastAddress ?? "" }
            focus = address.isEmpty ? .address : .key
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
