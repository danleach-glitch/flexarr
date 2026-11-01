import FlexarrKit
import SwiftUI

/// "Connect “Living room”?": a device showing a code asks to connect, and this phone (signed in as the main profile)
/// allows it or turns it away. Checking the code against the device's screen is what makes allowing it safe, so the
/// code is the biggest thing here.
struct PairingPromptView: View {
    @Environment(PairingPrompts.self) private var prompts
    let request: PairingRequest
    /// The admin PIN, once allowing it has turned out to take one.
    @State private var pin = ""
    @State private var asksPin = false

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
        // Taller once it takes the PIN, and staying so while it's checked.
        .presentationDetents([.height(asksPin ? 520 : 420)])
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
        if asksPin {
            MainProfilePinField(pin: $pin, problem: prompts.step.pinProblem, submit: allow)
        }

        Spacer(minLength: 0)

        VStack(spacing: 10) {
            Button(action: allow) {
                ZStack {
                    Text("Allow").opacity(prompts.step == .working ? 0 : 1)
                    if prompts.step == .working { ProgressView().tint(Color.room) }
                }
                .frame(maxWidth: .infinity)
            }
            .buttonStyle(ActionButtonStyle(prominent: true))
            .disabled(asksPin && !MainProfilePinField.isComplete(pin))
            Button {
                Task { await prompts.decline() }
            } label: {
                Text("Don't allow").frame(maxWidth: .infinity)
            }
            .buttonStyle(ActionButtonStyle())
        }
        .disabled(prompts.step == .working)
        .onChange(of: prompts.step.asksForPin, initial: true) { _, asks in if asks { asksPin = true } }
    }

    private func allow() {
        let entered = asksPin ? pin : nil
        pin = ""
        Task { await prompts.allow(pin: entered) }
    }

    @ViewBuilder private var allowed: some View {
        Spacer(minLength: 0)
        VStack(spacing: 14) {
            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: 52))
                .foregroundStyle(Color.go)
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
                .presentationBackground(Color.room)
                .presentationDragIndicator(.visible)
                .interactiveDismissDisabled(prompts.step == .working)
        }
        .banner(Binding(get: { prompts.notice }, set: { prompts.notice = $0 }), systemImage: "info.circle")
    }
}

extension PairingPrompts.Step {
    /// Allowing it waits for the admin PIN.
    var asksForPin: Bool {
        if case .needsPin = self { true } else { false }
    }

    /// What went wrong with the PIN last time (a wrong one, or too many).
    var pinProblem: String? {
        if case .needsPin(let problem) = self { problem } else { nil }
    }
}

/// The admin PIN, for allowing a device while the main profile is locked here (managing the server takes the
/// PIN, given lately, from Flexarr 0.56). 4 to 8 digits, on the number pad.
struct MainProfilePinField: View {
    @Binding var pin: String
    /// What went wrong last time: a wrong PIN, or too many.
    let problem: String?
    let submit: () -> Void

    @FocusState private var focused: Bool

    static func isComplete(_ pin: String) -> Bool { (4...8).contains(pin.count) }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Allowing it takes the admin PIN.")
                .font(.subheadline)
                .foregroundStyle(Color.screen)
            SecureField("PIN", text: $pin)
                .keyboardType(.numberPad)
                .textContentType(.oneTimeCode)
                .font(.title3.monospacedDigit())
                .padding(.horizontal, 14)
                .padding(.vertical, 10)
                .background(RoundedRectangle(cornerRadius: Radius.control).fill(Color.seat))
                .focused($focused)
                .submitLabel(.done)
                .onSubmit { if Self.isComplete(pin) { submit() } }
                .onChange(of: pin) { _, new in
                    let digits = String(new.filter(\.isNumber).prefix(8))
                    if digits != new { pin = digits }
                }
            if let problem {
                Label(problem, systemImage: "exclamationmark.triangle.fill")
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(Color.danger)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Text("Forgotten it? Reset it from Flexarr in a browser.")
                .font(.footnote)
                .foregroundStyle(Color.usher)
        }
        .onAppear { focused = true }
    }
}
