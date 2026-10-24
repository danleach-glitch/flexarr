import FlexarrKit
import SwiftUI

/// First run: server address and API key, validated against /system/status.
struct ConnectView: View {
    @Environment(AppModel.self) private var model
    @State private var address = ""
    @State private var apiKey = ""
    @State private var isConnecting = false
    @State private var error: String?
    @FocusState private var focus: Field?

    private enum Field { case address, key, connect }

    var body: some View {
        HStack(alignment: .center, spacing: 120) {
            VStack(alignment: .leading, spacing: 28) {
                Text("Flexarr")
                    .font(.marquee(150, weight: .black))
                    .foregroundStyle(Color.screen)
                Text("Your own cinema, on the big screen.")
                    .font(.system(size: 36, weight: .medium))
                    .foregroundStyle(Color.usher)
                Text("Connect to the Flexarr server on your network. You'll find the API key in the Flexarr web app under Settings > Server.")
                    .font(.system(size: 26))
                    .foregroundStyle(Color.usher)
                    .frame(maxWidth: 640, alignment: .leading)
                    .padding(.top, 20)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

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
                }
                .padding(.top, 8)

                if let error {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.system(size: 25, weight: .medium))
                        .foregroundStyle(Color.screen)
                        .padding(24)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(RoundedRectangle(cornerRadius: 14).fill(Color(red: 0.42, green: 0.2, blue: 0.28).opacity(0.6)))
                        .transition(.opacity)
                }
            }
            .frame(width: 760)
            .focusSection()
        }
        .padding(.horizontal, 120)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoomBackground(ambient: .silver, intensity: 0.22))
        .animation(.easeOut(duration: 0.2), value: error)
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
