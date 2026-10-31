import CoreImage.CIFilterBuiltins
import FlexarrKit
import SwiftUI
import UIKit

/// First run. Servers on the network are listed (they announce themselves), so there's nothing to type: pick one and
/// the TV shows a code and QR code to approve from a phone or computer signed in to Flexarr. Typing an address is
/// still there for servers that can't be found (in Docker, or over Tailscale), and leads to the same code.
struct ConnectView: View {
    @Environment(AppModel.self) private var model
    @State private var browser = ServerBrowser()
    @State private var step: Step = .choose

    private enum Step: Equatable {
        case choose
        case pair(DiscoveredServer)
        case manual
        /// A family invite: this TV shows a code to read to whoever invited it.
        case guest
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
                Text("Your entertainment, on every screen.")
                    .font(.system(size: 36, weight: .medium))
                    .foregroundStyle(Color.usher)
                if let notice = model.signedOutNotice {
                    Text(notice)
                        .font(.system(size: 32, weight: .semibold))
                        .foregroundStyle(Color.screen)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.top, 20)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            Group {
                switch step {
                case .choose:
                    ChooseServer(browser: browser, onPick: { step = .pair($0) }, onManual: { step = .manual }, onGuest: { step = .guest })
                case .pair(let server):
                    PairWithServer(server: server, onBack: { step = .choose })
                case .manual:
                    ManualConnect(onPair: { step = .pair($0) }, onBack: { step = .choose })
                case .guest:
                    GuestCode(onBack: { step = .choose })
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
            // Signed out on the server: show a code for the same server straight away.
            if model.signedOutNotice != nil, let server = model.store.lastAddress.flatMap(FlexarrClient.normalizeAddress) {
                step = .pair(DiscoveredServer(name: server.host() ?? "Flexarr", address: server))
            }
            #if DEBUG
            // `-FlexarrPairWith <url>` opens the code screen directly (screenshots and UI tests).
            if let server = UserDefaults.standard.string(forKey: "FlexarrPairWith").flatMap(FlexarrClient.normalizeAddress) {
                step = .pair(DiscoveredServer(name: server.host() ?? "Flexarr", address: server))
            }
            // `-FlexarrGuestCode YES` opens "Join someone’s Flexarr" directly.
            if UserDefaults.standard.bool(forKey: "FlexarrGuestCode") { step = .guest }
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
    let onGuest: () -> Void
    @FocusState private var focused: String?
    /// Nothing has turned up for a while: say what to check.
    @State private var lookingAWhile = false

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
                if lookingAWhile {
                    Text("Can’t see your server? Check the Apple TV is on the same Wi-Fi as it, or enter an address.")
                        .font(.system(size: 28, weight: .medium))
                        .foregroundStyle(Color.screen)
                        .fixedSize(horizontal: false, vertical: true)
                }
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
            Button("Join someone’s Flexarr", action: onGuest)
                .buttonStyle(ActionButtonStyle(prominent: false))
        }
        // The first server found takes focus, so one click of the remote picks it.
        .onChange(of: browser.servers.first?.id, initial: true) { _, first in
            if focused == nil, let first { focused = first }
        }
        .task {
            guard (try? await Task.sleep(for: .seconds(10))) != nil else { return }
            lookingAWhile = true
        }
    }
}

// MARK: - Approve with a phone

private struct PairWithServer: View {
    @Environment(AppModel.self) private var model
    let server: DiscoveredServer
    let onBack: () -> Void
    @State private var pairing: Pairing?
    /// Try again after "Don't allow": a fresh start, with a fresh code the phones are asked about.
    @State private var attempt = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 30) {
            if let pairing, pairing.declined {
                VStack(alignment: .leading, spacing: 18) {
                    Text("Not allowed")
                        .font(.system(size: 40, weight: .bold))
                        .foregroundStyle(Color.screen)
                    Text("The main profile turned this Apple TV away. If that was a mistake, try again and allow it this time.")
                        .font(.system(size: 28))
                        .foregroundStyle(Color.usher)
                        .frame(maxWidth: 1100, alignment: .leading)
                }
            } else if let pairing, let start = pairing.current {
                // The iPhone's steps, word for word: a phone signed in as the main profile is asked to allow it,
                // and the web app does it without one. A new server has nobody to allow it yet, so setting it up in a
                // browser comes first; the QR code only helps a phone that's signed in already, so it's small,
                // underneath.
                VStack(alignment: .leading, spacing: 26) {
                    Text("Connect this Apple TV")
                        .font(.system(size: 40, weight: .bold))
                        .foregroundStyle(Color.screen)
                    if pairing.needsSetup {
                        VStack(alignment: .leading, spacing: 20) {
                            NumberedStep(1) {
                                Text("On your computer, open **\(server.address.absoluteString)** in a web browser.")
                            }
                            NumberedStep(2) {
                                Text(pairing.setupNeedsCode
                                    ? "Type in the setup code from the server's log, then click **Set up Flexarr**."
                                    : "Click **Set up Flexarr**.")
                            }
                            NumberedStep(3) { Text("Go to **Settings**, then **Access**, then **Connect a device**.") }
                            NumberedStep(4) { Text("Type in this code:") }
                        }
                        code(start, renewed: pairing.renewedAt != nil)
                    } else {
                        Text("On a phone signed in to Flexarr, tap **Allow** when it asks. Check it shows this code:")
                            .font(.system(size: 30))
                            .foregroundStyle(Color.screen)
                            .fixedSize(horizontal: false, vertical: true)
                        code(start, renewed: pairing.renewedAt != nil)
                        Text("Or on a computer, open **\(server.address.absoluteString)**, go to **Settings › Access › Connect a device** and type in the code.")
                            .font(.system(size: 26))
                            .foregroundStyle(Color.usher)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    HStack(spacing: 14) {
                        ProgressView()
                        Text("This screen moves on by itself once it's allowed.")
                            .font(.system(size: 24))
                            .foregroundStyle(Color.usher)
                    }
                }
                if !pairing.needsSetup {
                    HStack(spacing: 20) {
                        QRCode(text: pairing.link(for: start).absoluteString)
                            .frame(width: 140, height: 140)
                        Text("Or scan with a phone that’s signed in to Flexarr.")
                            .font(.system(size: 24))
                            .foregroundStyle(Color.usher)
                    }
                }
            } else if pairing?.error == nil {
                ProgressView()
            }
            if let error = pairing?.error {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .font(.system(size: 25, weight: .medium))
                    .foregroundStyle(Color.screen)
            }
            HStack(spacing: 30) {
                // First, so it has the focus.
                if let pairing, pairing.declined {
                    Button("Try again") { attempt += 1 }
                        .buttonStyle(ActionButtonStyle(prominent: true))
                }
                Button("Back", action: onBack)
                    .buttonStyle(ActionButtonStyle(prominent: false))
            }
            .focusSection()
        }
        .task(id: "\(server.address.absoluteString)#\(attempt)") {
            let pairing = Pairing(server: server)
            self.pairing = pairing
            await pairing.run(model: model)
        }
    }

    private func code(_ start: PairingStart, renewed: Bool) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 20) {
            // Regular width, as the web pages show codes (SF Pro Black), so the two read the same.
            Text(start.code)
                .font(.system(size: 88, weight: .black))
                .foregroundStyle(Color.screen)
                .kerning(6)
                .fixedSize()
            if renewed {
                Text("A new code")
                    .font(.system(size: 30, weight: .semibold))
                    .foregroundStyle(Color.silver)
            }
        }
    }
}

/// One step of the connect instructions: a number in a circle, then what to do (as on the iPhone).
private struct NumberedStep<Content: View>: View {
    let number: Int
    @ViewBuilder let content: Content

    init(_ number: Int, @ViewBuilder content: () -> Content) {
        self.number = number
        self.content = content()
    }

    var body: some View {
        HStack(alignment: .top, spacing: 18) {
            Text("\(number)")
                .font(.system(size: 24, weight: .bold))
                .foregroundStyle(Color.room)
                .frame(width: 42, height: 42)
                .background(Circle().fill(Color.silver))
            content
                .font(.system(size: 30))
                .foregroundStyle(Color.screen)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 3)
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

/// A server that can't be found (in Docker, or over Tailscale): type its address and connect with a code like any
/// other. The API key is only for connecting from outside the home network and tailnet, where codes aren't offered.
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
                    .submitLabel(usesKey ? .next : .go)
                    .onSubmit { if usesKey { focus = .key } else { connect() } }
            }
            if usesKey {
                field("API key") {
                    SecureField("Paste or type your API key", text: $apiKey)
                        .focused($focus, equals: .key)
                        .submitLabel(.go)
                        .onSubmit { connect() }
                }
            } else {
                Text("You’ll get a code to approve, like a server found on your network.")
                    .font(.system(size: 24))
                    .foregroundStyle(Color.usher)
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

            if !usesKey {
                Button("Connecting from outside your home? Use an API key") {
                    usesKey = true
                    focus = .key
                }
                .buttonStyle(ActionButtonStyle(prominent: false))
            }
        }
        .onAppear {
            if address.isEmpty { address = model.store.lastAddress ?? "" }
            focus = address.isEmpty ? .address : .connect
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

// MARK: - A family invite, by code

/// "Join someone’s Flexarr": the code to read to whoever invited this Apple TV (see `GuestCodeWaiter`).
private struct GuestCode: View {
    @Environment(AppModel.self) private var model
    let onBack: () -> Void
    @State private var waiter = GuestCodeWaiter()

    var body: some View {
        VStack(alignment: .leading, spacing: 30) {
            if let joining = waiter.joining {
                HStack(spacing: 20) {
                    ProgressView()
                    Text("Joining \(joining.ownerName)’s Flexarr…")
                        .font(.system(size: 32, weight: .semibold))
                        .foregroundStyle(Color.screen)
                }
            } else {
                Text("Read this code to the person who invited you")
                    .font(.system(size: 32, weight: .semibold))
                    .foregroundStyle(Color.usher)
                    .fixedSize(horizontal: false, vertical: true)
                Text(waiter.code?.display ?? "····-····")
                    .font(.system(size: 110, weight: .bold, design: .monospaced))
                    .foregroundStyle(Color.screen)
                    .accessibilityLabel(waiter.code.map { $0.code.map(String.init).joined(separator: " ") } ?? "Getting a code")
                Text("They type it into Settings › Access in Flexarr.")
                    .font(.system(size: 26))
                    .foregroundStyle(Color.usher)
            }
            if let problem = waiter.problem {
                Text(problem)
                    .font(.system(size: 28, weight: .medium))
                    .foregroundStyle(Color.screen)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Button("Back", action: onBack)
                .buttonStyle(ActionButtonStyle(prominent: false))
                .disabled(waiter.joining != nil)
        }
        .task { await waiter.run(model: model) }
    }
}
