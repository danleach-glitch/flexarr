import FlexarrKit
import SwiftUI

/// The profile's picture, or its initial on its colour.
struct ProfileAvatar: View {
    @Environment(AppModel.self) private var model
    let profile: Profile
    var size: CGFloat = 96

    var body: some View {
        ZStack {
            Circle().fill(Color(rgb: RGB(hex: profile.color) ?? .silver))
            Text(profile.initial)
                .font(.marquee(size * 0.46, weight: .heavy))
                .foregroundStyle(Color.room)
            if let url = model.client?.imageURL(profile.image, width: Int(size * 3)) {
                RemoteImage(url: url) { Color.clear }
                    .clipShape(Circle())
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

/// "Who's watching?": avatars to tap, then a PIN for a protected profile. Full screen on first launch when the
/// household has several profiles, and a sheet from the avatar on Home or from Settings afterwards.
struct ProfilePickerView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    /// Opened to switch: the profile watching now is marked and can't be chosen again.
    var marksCurrent = false

    @State private var asking: Profile?
    @State private var switching: String?
    @State private var error: String?

    var body: some View {
        ZStack {
            RoomBackground(ambient: nil)
            if let asking {
                PinEntry(profile: asking, error: $error, onCancel: { self.asking = nil; error = nil }) { pin in
                    await choose(asking, pin: pin)
                }
                .transition(.opacity)
            } else {
                choices.transition(.opacity)
            }
        }
        .animation(.easeInOut(duration: 0.2), value: asking)
    }

    private var choices: some View {
        ScrollView {
            VStack(spacing: 32) {
                Text("Who’s watching?")
                    .font(.marquee(40, weight: .heavy))
                    .foregroundStyle(Color.screen)
                    .padding(.top, marksCurrent ? 28 : 80)

                LazyVGrid(columns: [GridItem(.adaptive(minimum: 110, maximum: 150), spacing: 20)], spacing: 28) {
                    ForEach(model.profiles) { profile in
                        Button { Task { await press(profile) } } label: {
                            VStack(spacing: 10) {
                                ProfileAvatar(profile: profile)
                                    .overlay {
                                        if switching == profile.id {
                                            Circle().fill(Color.black.opacity(0.45))
                                            ProgressView()
                                        }
                                    }
                                    .overlay {
                                        if marksCurrent && profile.isCurrent {
                                            Circle().strokeBorder(Color.screen, lineWidth: 3).padding(-6)
                                        }
                                    }
                                VStack(spacing: 2) {
                                    Text(profile.name)
                                        .font(.body.weight(.semibold))
                                        .foregroundStyle(Color.screen)
                                    Text(caption(for: profile) ?? " ")
                                        .font(.caption.weight(.medium))
                                        .foregroundStyle(Color.usher)
                                }
                                .lineLimit(1)
                            }
                        }
                        .buttonStyle(CardButtonStyle())
                        .disabled(switching != nil || (marksCurrent && profile.isCurrent))
                        .accessibilityLabel([profile.name, caption(for: profile)].compactMap { $0 }.joined(separator: ", "))
                    }
                }
                .padding(.horizontal, 32)

                if let error {
                    Text(error)
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(Color.danger)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, 32)
                }
            }
            .frame(maxWidth: .infinity)
        }
    }

    private func caption(for profile: Profile) -> String? {
        if marksCurrent && profile.isCurrent { return "Watching now" }
        if profile.hasPin { return "PIN" }
        return nil
    }

    private func press(_ profile: Profile) async {
        error = nil
        if profile.hasPin {
            asking = profile
            return
        }
        await choose(profile, pin: nil)
    }

    private func choose(_ profile: Profile, pin: String?) async {
        switching = profile.id
        defer { switching = nil }
        do {
            try await model.switchProfile(to: profile, pin: pin)
            if marksCurrent { dismiss() }
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}

/// A 4 to 8 digit PIN, on the number pad.
private struct PinEntry: View {
    let profile: Profile
    @Binding var error: String?
    let onCancel: () -> Void
    let onSubmit: (String) async -> Void

    @State private var pin = ""
    @State private var checking = false
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 22) {
            ProfileAvatar(profile: profile, size: 84)
            Text("Enter \(profile.name)’s PIN")
                .font(.marquee(32, weight: .heavy))
                .foregroundStyle(Color.screen)
            SecureField("PIN", text: $pin)
                .keyboardType(.numberPad)
                .textContentType(.oneTimeCode)
                .multilineTextAlignment(.center)
                .font(.title2.monospacedDigit())
                .padding(.vertical, 12)
                .frame(maxWidth: 220)
                .background(RoundedRectangle(cornerRadius: 12).fill(Color.seat))
                .focused($focused)
                .onChange(of: pin) { _, new in
                    let digits = String(new.filter(\.isNumber).prefix(8))
                    if digits != new { pin = digits }
                    error = nil
                }
            Text(error ?? (checking ? "Checking" : " "))
                .font(.subheadline.weight(.medium))
                .foregroundStyle(error == nil ? Color.usher : Color.danger)
                .multilineTextAlignment(.center)
            HStack(spacing: 12) {
                Button("Cancel", action: onCancel)
                    .buttonStyle(ActionButtonStyle())
                Button("Continue", action: submit)
                    .buttonStyle(ActionButtonStyle(prominent: true))
                    .disabled(pin.count < 4 || checking)
            }
        }
        .padding(32)
        .onAppear { focused = true }
    }

    private func submit() {
        guard pin.count >= 4, !checking else { return }
        let entered = pin
        checking = true
        Task {
            await onSubmit(entered)
            checking = false
            // Only reached on a wrong PIN; a right one replaces this screen.
            pin = ""
            focused = true
        }
    }
}
