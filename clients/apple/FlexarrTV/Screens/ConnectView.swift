import CoreImage.CIFilterBuiltins
import FlexarrKit
import SwiftUI
import UIKit

/// First run. Servers on the network are listed (they announce themselves), so there's nothing to type: pick one and
/// the TV shows a code and QR code to approve from a phone or computer signed in to Flexarr. Typing an address (and
/// API key) is still there for servers that can't be found, such as over Tailscale.
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
        HStack(alignment: .center, spacing: 120) {
            VStack(alignment: .leading, spacing: 28) {
                HStack(alignment: .center, spacing: 28) {
                    BrandMark(size: 132)
                    Text("flexarr")
                        .font(.marquee(150, weight: .black))
                        .foregroundStyle(Color.screen)
                }
                Text("Your own cinema, on the big screen.")
                    .font(.system(size: 36, weight: .medium))
                    .foregroundStyle(Color.usher)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            Group {
                switch step {
                case .choose:
                    ChooseServer(browser: browser, onPick: { step = .pair($0) }, onManual: { step = .manual })
                case .pair(let server):
                    PairWithServer(server: server, onBack: { step = .choose })
                case .manual:
                    ManualConnect(onBack: { step = .choose })
                }
            }
            .frame(width: 820)
            .focusSection()
        }
        .padding(.horizontal, 120)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoomBackground(ambient: .silver, intensity: 0.22))
        .onAppear {
            browser.start()
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
    @FocusState private var focused: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 30) {
            Text("Servers on your network")
                .font(.system(size: 30, weight: .semibold))
                .foregroundStyle(Color.usher)
            if browser.servers.isEmpty {
                HStack(spacing: 20) {
                    ProgressView()
                    Text("Looking for Flexarr…")
                        .font(.system(size: 28))
                        .foregroundStyle(Color.usher)
                }
                .padding(.vertical, 20)
            } else {
                ForEach(browser.servers) { server in
                    Button {
                        onPick(server)
                    } label: {
                        Label(server.name, systemImage: "server.rack")
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .buttonStyle(ActionButtonStyle(prominent: true))
                    .focused($focused, equals: server.id)
                }
            }
            Button("Enter an address instead", action: onManual)
                .buttonStyle(ActionButtonStyle(prominent: false))
                .padding(.top, 10)
        }
        // The first server found takes focus, so one click of the remote picks it.
        .onChange(of: browser.servers.first?.id, initial: true) { _, first in
            if focused == nil, let first { focused = first }
        }
    }
}

// MARK: - Approve with a phone

private struct PairWithServer: View {
    @Environment(AppModel.self) private var model
    let server: DiscoveredServer
    let onBack: () -> Void
    @State private var pairing: PairingStart?
    @State private var error: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 30) {
            if let pairing {
                HStack(alignment: .top, spacing: 44) {
                    QRCode(text: link(for: pairing).absoluteString)
                        .frame(width: 300, height: 300)
                    VStack(alignment: .leading, spacing: 18) {
                        Text("Scan with your phone")
                            .font(.system(size: 34, weight: .semibold))
                            .foregroundStyle(Color.screen)
                        Text("It opens Flexarr; tap Allow. Or, on any device signed in to Flexarr, open Settings › Connect a TV and enter:")
                            .font(.system(size: 25))
                            .foregroundStyle(Color.usher)
                            .fixedSize(horizontal: false, vertical: true)
                        Text(pairing.code)
                            .font(.marquee(96, weight: .black))
                            .foregroundStyle(Color.screen)
                            .kerning(6)
                    }
                }
                Text("Connecting to \(server.name). Waiting for approval…")
                    .font(.system(size: 24))
                    .foregroundStyle(Color.usher)
            } else if error == nil {
                ProgressView()
            }
            if let error {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.system(size: 25, weight: .medium))
                    .foregroundStyle(Color.screen)
            }
            Button("Back", action: onBack)
                .buttonStyle(ActionButtonStyle(prominent: false))
        }
        .task(id: server) { await pair() }
    }

    private func link(for pairing: PairingStart) -> URL {
        server.address.appending(path: "link").appending(queryItems: [URLQueryItem(name: "code", value: pairing.code)])
    }

    /// Signs in with the token the server just handed over. It's only handed over once, so a network blip here
    /// is retried a few times rather than starting over with a new code (and leaving a sign-in nobody holds).
    private func connect(with token: String) async throws {
        var attempt = 0
        while true {
            do {
                try await model.connect(address: server.address.absoluteString, apiKey: token)
                return
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                attempt += 1
                guard attempt < 5 else {
                    // Give up on this one: sign it out (best effort) so it doesn't linger in the list.
                    try? await FlexarrClient(baseURL: server.address, apiKey: token).signOut()
                    throw error
                }
                self.error = "Connecting… (\(ConnectionError(error).message))"
                try await Task.sleep(for: .seconds(2 * attempt))
            }
        }
    }

    /// Gets a code, then polls until it's approved (and signs in) or expires (and gets a new one).
    private func pair() async {
        let client = FlexarrClient(baseURL: server.address)
        while !Task.isCancelled {
            do {
                let started = try await client.startPairing(deviceName: UIDevice.current.name)
                pairing = started
                error = nil
                while !Task.isCancelled {
                    try await Task.sleep(for: .seconds(2))
                    let status = try await client.pairingStatus(started.id)
                    if status.status == "approved", let key = status.apiKey {
                        try await connect(with: key)
                        return
                    }
                    if status.status == "expired" { break } // a fresh code next time round
                }
            } catch is CancellationError {
                return
            } catch {
                self.error = ConnectionError(error).message
                try? await Task.sleep(for: .seconds(5))
            }
        }
    }
}

/// A QR code, drawn crisp at any size.
private struct QRCode: View {
    let text: String

    var body: some View {
        if let image = Self.image(text) {
            Image(uiImage: image)
                .interpolation(.none)
                .resizable()
                .padding(18)
                .background(RoundedRectangle(cornerRadius: 18).fill(.white))
        }
    }

    static func image(_ text: String) -> UIImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage, let cg = CIContext().createCGImage(output, from: output.extent) else { return nil }
        return UIImage(cgImage: cg)
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

    private enum Field { case address, key, connect }

    var body: some View {
        VStack(alignment: .leading, spacing: 34) {
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
                SecureField("Paste or type your API key", text: $apiKey)
                    .focused($focus, equals: .key)
                    .submitLabel(.go)
                    .onSubmit { connect() }
            }

            HStack(spacing: 24) {
                Button(action: connect) {
                    if isConnecting {
                        HStack(spacing: 16) {
                            ProgressView()
                            Text("Connecting")
                        }
                    } else {
                        Label("Connect", systemImage: "arrow.right.circle.fill")
                    }
                }
                .buttonStyle(ActionButtonStyle(prominent: true))
                .focused($focus, equals: .connect)
                .disabled(isConnecting || address.trimmingCharacters(in: .whitespaces).isEmpty)
                Button("Back", action: onBack)
                    .buttonStyle(ActionButtonStyle(prominent: false))
            }
            .padding(.top, 8)

            if let error {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.system(size: 25, weight: .medium))
                    .foregroundStyle(Color.screen)
                    .padding(24)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(RoundedRectangle(cornerRadius: 14).fill(Color(red: 0.42, green: 0.2, blue: 0.28).opacity(0.6)))
            }
        }
        .onAppear {
            if address.isEmpty { address = model.store.lastAddress ?? "" }
            focus = address.isEmpty ? .address : .key
        }
    }

    private func field(_ title: String, @ViewBuilder content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title)
                .font(.system(size: 24, weight: .semibold))
                .foregroundStyle(Color.usher)
            content()
                .font(.system(size: 30))
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
