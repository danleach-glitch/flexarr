import FlexarrKit
import SwiftUI

/// "Connect “Living room”?": a device showing a code asks to connect, and this phone (signed in as the main profile)
/// allows it or turns it away. Checking the code against the device's screen is what makes allowing it safe, so the
/// code is the biggest thing here.
struct PairingPromptView: View {
    @Environment(PairingPrompts.self) private var prompts
    let request: PairingRequest

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            if prompts.step == .allowed {
                allowed
            } else {
                asking
            }
        }
        .padding(.horizontal, 24)
        .padding(.top, 28)
        .padding(.bottom, 16)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .animation(.easeInOut(duration: 0.2), value: prompts.step)
    }

    @ViewBuilder private var asking: some View {
        VStack(alignment: .leading, spacing: 6) {
            Image(systemName: request.symbol)
                .font(.system(size: 26, weight: .semibold))
                .foregroundStyle(Color.screen)
                .accessibilityHidden(true)
                .padding(.bottom, 4)
            Text("Connect “\(request.deviceName)”?")
                .font(.title2.weight(.bold))
                .foregroundStyle(Color.screen)
                .fixedSize(horizontal: false, vertical: true)
            if let whereFrom = request.whereFrom {
                Text(whereFrom)
                    .font(.subheadline)
                    .foregroundStyle(Color.usher)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }

        VStack(alignment: .leading, spacing: 4) {
            Text("Check its screen shows")
                .font(.body)
                .foregroundStyle(Color.screen)
            // As the connect screens and the web pages show codes: regular width, SF Pro Black.
            Text(request.code)
                .font(.system(size: 52, weight: .black))
                .kerning(2)
                .foregroundStyle(Color.screen)
                .accessibilityLabel(request.code.map(String.init).joined(separator: " "))
        }

        if case .failed(let message) = prompts.step {
            Label(message, systemImage: "exclamationmark.triangle.fill")
                .font(.subheadline.weight(.medium))
                .foregroundStyle(Color.danger)
                .fixedSize(horizontal: false, vertical: true)
        }

        Spacer(minLength: 0)

        VStack(spacing: 10) {
            Button {
                Task { await prompts.allow() }
            } label: {
                ZStack {
                    Text("Allow").opacity(prompts.step == .working ? 0 : 1)
                    if prompts.step == .working { ProgressView().tint(Color.room) }
                }
                .frame(maxWidth: .infinity)
            }
            .buttonStyle(ActionButtonStyle(prominent: true))
            Button("Don't allow") {
                Task { await prompts.decline() }
            }
            .buttonStyle(ActionButtonStyle())
            .frame(maxWidth: .infinity)
        }
        .disabled(prompts.step == .working)
    }

    @ViewBuilder private var allowed: some View {
        Spacer(minLength: 0)
        VStack(spacing: 14) {
            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: 52))
                .foregroundStyle(Color.downloaded)
            Text("Connected")
                .font(.title2.weight(.bold))
                .foregroundStyle(Color.screen)
            Text("It'll be ready in a moment.")
                .font(.body)
                .foregroundStyle(Color.usher)
        }
        .frame(maxWidth: .infinity)
        .transition(.opacity)
        Spacer(minLength: 0)
    }
}

extension View {
    /// Asks about devices waiting to connect, one at a time, over whatever's on screen. Swiping one away leaves it
    /// waiting; its code running out (`PairingPrompts` keeps the time), or it being allowed somewhere else, closes it.
    func pairingPrompts(_ prompts: PairingPrompts) -> some View {
        sheet(item: Binding(
            get: { prompts.current },
            set: { if $0 == nil { prompts.setAsideCurrent() } }
        )) { request in
            PairingPromptView(request: request)
                .environment(prompts)
                .presentationDetents([.height(420)])
                .presentationBackground(Color.room)
                .presentationDragIndicator(.visible)
                .interactiveDismissDisabled(prompts.step == .working)
        }
        .alert(prompts.notice ?? "", isPresented: Binding(
            get: { prompts.notice != nil },
            set: { if !$0 { prompts.notice = nil } }
        )) {
            Button("OK", role: .cancel) {}
        }
    }
}
