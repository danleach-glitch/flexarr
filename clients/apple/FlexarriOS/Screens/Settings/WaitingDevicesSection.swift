import FlexarrKit
import SwiftUI

/// "Devices waiting to connect", on Settings for the main profile: each device showing a code, to allow or turn away
/// here (one swiped off the prompt stays waiting, and turns up here). The same `PairingPrompts` as the prompt, so
/// answering one in either place answers it in both.
struct WaitingDevicesSection: View {
    @Environment(PairingPrompts.self) private var pairing
    @Environment(PushNotifications.self) private var notifications

    var body: some View {
        Section {
            if pairing.waiting.isEmpty {
                Text("No devices are waiting. When one asks to connect, it shows here and your phone asks you.")
                    .font(.subheadline)
                    .foregroundStyle(Color.usher)
                    .listRowBackground(Color.seat)
            } else {
                ForEach(pairing.waiting) { request in
                    WaitingDeviceRow(request: request, step: pairing.step(for: request.code))
                        .listRowBackground(Color.seat)
                }
            }
        } header: {
            Text("Devices waiting to connect")
        } footer: {
            // Without them, a device asking is only heard about while Flexarr is open here.
            if !notifications.isOn {
                Text("Turn on Notifications to be asked when Flexarr isn't open.")
            }
        }
        .animation(.easeInOut(duration: 0.2), value: pairing.waiting)
    }
}

/// One device: what it is, where it's asking from and its code to check against its screen, then the answer.
private struct WaitingDeviceRow: View {
    @Environment(PairingPrompts.self) private var pairing
    let request: PairingRequest
    let step: PairingPrompts.Step

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 14) {
                SettingsIcon(request.symbol)
                VStack(alignment: .leading, spacing: 2) {
                    Text(request.deviceName).font(.body.weight(.semibold))
                    if let kind = request.kindAndNetwork {
                        Text(kind).font(.subheadline).foregroundStyle(Color.usher)
                    }
                    if !request.asks {
                        Text("You turned this away. Allow it if it's yours.")
                            .font(.subheadline)
                            .foregroundStyle(Color.usher)
                    }
                }
                Spacer(minLength: 8)
                Text(request.code)
                    .font(.body.weight(.black))
                    .kerning(1)
                    .accessibilityLabel(request.code.map(String.init).joined(separator: " "))
            }

            switch step {
            case .allowed:
                Label("Connected", systemImage: "checkmark.circle.fill")
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(Color.downloaded)
            default:
                if case .failed(let message) = step {
                    Label(message, systemImage: "exclamationmark.triangle.fill")
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(Color.danger)
                        .fixedSize(horizontal: false, vertical: true)
                }
                HStack(spacing: 10) {
                    Button {
                        Task { await pairing.allow(request.code) }
                    } label: {
                        ZStack {
                            Text("Allow").opacity(step == .working ? 0 : 1)
                            if step == .working { ProgressView().tint(Color.room) }
                        }
                        .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(ActionButtonStyle(prominent: true))
                    Button {
                        Task { await pairing.decline(request.code) }
                    } label: {
                        Text("Don't allow").frame(maxWidth: .infinity)
                    }
                    .buttonStyle(ActionButtonStyle())
                }
                .disabled(step == .working)
            }
        }
        .padding(.vertical, 6)
    }
}
