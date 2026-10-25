import FlexarrKit
import SwiftUI

/// The profile's picture, or its initial on its colour.
struct ProfileAvatar: View {
    @Environment(AppModel.self) private var model
    let profile: Profile
    var size: CGFloat = 220

    var body: some View {
        ZStack {
            Circle().fill(Color(rgb: RGB(hex: profile.color) ?? .silver))
            Text(profile.initial)
                .font(.marquee(size * 0.46, weight: .heavy))
                .foregroundStyle(Color.room)
            if let url = model.client?.imageURL(profile.image, width: Int(size * 2)) {
                // Sized before clipping, so a picture that isn't square fills the circle rather than
                // growing past it (the initial stays underneath for a picture that doesn't arrive).
                RemoteImage(url: url) { Color.clear }
                    .frame(width: size, height: size)
                    .clipShape(Circle())
            }
        }
        .frame(width: size, height: size)
    }
}

/// "Who's watching?": a row of avatars, then a PIN pad for a protected profile. Shown full screen
/// on first launch when the household has several profiles, and from the avatar in the top right
/// corner afterwards. With two profiles that opens focused on the other one, so switching is a single press.
struct ProfilePickerView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    /// Opened from the avatar: the profile watching now is marked and the other one gets focus.
    var marksCurrent = false

    @State private var asking: Profile?
    @State private var switching: String?
    @State private var error: String?
    @FocusState private var focused: String?

    var body: some View {
        VStack(spacing: 70) {
            if let asking {
                PinPad(profile: asking, error: $error, onCancel: { cancelPin(asking) }) { pin in
                    await choose(asking, pin: pin)
                }
                .transition(.opacity)
            } else {
                choices.transition(.opacity)
            }
        }
        .animation(.easeInOut(duration: 0.25), value: asking)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoomBackground(ambient: nil))
    }

    private var choices: some View {
        VStack(spacing: 70) {
            Text("Who’s watching?")
                .font(.marquee(76, weight: .heavy))
                .foregroundStyle(Color.screen)

            HStack(spacing: 80) {
                ForEach(model.profiles) { profile in
                    Button {
                        Task { await press(profile) }
                    } label: {
                        ProfileChoice(profile: profile, caption: caption(for: profile),
                                      busy: switching == profile.id, isFocused: focused == profile.id)
                    }
                    .buttonStyle(ProfileButtonStyle())
                    .focused($focused, equals: profile.id)
                    // Whoever's watching stays a stop for the remote (disabling it left a two-profile
                    // household with nowhere to move); choosing them again just closes the picker.
                    // Focus starts on someone else, so with two profiles one press switches.
                    .disabled(switching != nil)
                }
            }
            .defaultFocus($focused, defaultFocus)

            if let error {
                Text(error)
                    .font(.system(size: 28, weight: .medium))
                    .foregroundStyle(Color.screen)
            }
        }
    }

    private var defaultFocus: String? {
        guard marksCurrent else { return model.profiles.first?.id }
        return (model.otherProfile ?? model.profiles.first(where: { !$0.isCurrent }))?.id
    }

    private func caption(for profile: Profile) -> String? {
        if marksCurrent && profile.isCurrent { return "Watching now" }
        if profile.hasPin { return "PIN" }
        if marksCurrent && profile.id == model.otherProfile?.id { return "Switch" }
        return nil
    }

    private func press(_ profile: Profile) async {
        error = nil
        if marksCurrent && profile.isCurrent {
            // Already watching: nothing to switch, so this is the same as backing out.
            dismiss()
            return
        }
        if profile.hasPin {
            asking = profile
            return
        }
        await choose(profile, pin: nil)
    }

    private func cancelPin(_ profile: Profile) {
        asking = nil
        error = nil
        // The avatar was replaced by the keypad, so focus has to be handed back.
        Task {
            try? await Task.sleep(for: .milliseconds(50))
            focused = profile.id
        }
    }

    private func choose(_ profile: Profile, pin: String?) async {
        switching = profile.id
        defer { switching = nil }
        do {
            try await model.switchProfile(to: profile, pin: pin)
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}

private struct ProfileChoice: View {
    let profile: Profile
    let caption: String?
    let busy: Bool
    let isFocused: Bool

    var body: some View {
        VStack(spacing: 28) {
            ProfileAvatar(profile: profile)
                .overlay {
                    if busy {
                        Circle().fill(Color.black.opacity(0.45))
                        ProgressView()
                    }
                }
            VStack(spacing: 8) {
                Text(profile.name)
                    .font(.system(size: 34, weight: .semibold))
                    .foregroundStyle(isFocused ? Color.screen : Color.usher)
                Text(caption ?? " ")
                    .font(.system(size: 24, weight: .medium))
                    .foregroundStyle(Color.usher)
            }
            .lineLimit(1)
        }
        .frame(width: 280)
    }
}

/// Avatars lift and gain a ring in projected white on focus, rather than the card's rectangle.
private struct ProfileButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        ProfileButtonBody(configuration: configuration)
    }

    private struct ProfileButtonBody: View {
        let configuration: Configuration
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .overlay(alignment: .top) {
                    Circle()
                        .strokeBorder(Color.screen, lineWidth: 8)
                        .frame(width: 236, height: 236)
                        .offset(y: -8)
                        .opacity(isFocused ? 1 : 0)
                }
                .scaleEffect(configuration.isPressed ? 1.02 : (isFocused ? 1.1 : 1))
                .shadow(color: isFocused ? Color.black.opacity(0.5) : .clear, radius: 30, y: 18)
                .animation(.spring(response: 0.3, dampingFraction: 0.7), value: isFocused)
                .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
        }
    }
}

/// A 4 to 8 digit PIN entered with the remote: a keypad, since the Siri Remote has no number keys.
private struct PinPad: View {
    let profile: Profile
    @Binding var error: String?
    let onCancel: () -> Void
    let onSubmit: (String) async -> Void

    @State private var pin = ""
    @State private var checking = false
    @FocusState private var focusedKey: String?

    private let rows = [["1", "2", "3"], ["4", "5", "6"], ["7", "8", "9"], ["delete", "0", "ok"]]

    var body: some View {
        HStack(spacing: 120) {
            VStack(spacing: 30) {
                ProfileAvatar(profile: profile, size: 180)
                Text("Enter \(profile.name)’s PIN")
                    .font(.marquee(56, weight: .heavy))
                    .foregroundStyle(Color.screen)
                HStack(spacing: 22) {
                    ForEach(0..<max(4, pin.count), id: \.self) { index in
                        Circle()
                            .fill(index < pin.count ? Color.screen : Color.clear)
                            .overlay { Circle().strokeBorder(Color.usher, lineWidth: 3) }
                            .frame(width: 30, height: 30)
                    }
                }
                .frame(height: 40)
                Text(error ?? (checking ? "Checking" : " "))
                    .font(.system(size: 28, weight: .medium))
                    .foregroundStyle(error == nil ? Color.screen : Color.danger)
            }
            .frame(width: 640)

            Grid(horizontalSpacing: 26, verticalSpacing: 26) {
                ForEach(rows, id: \.self) { row in
                    GridRow {
                        ForEach(row, id: \.self) { key in
                            Button { press(key) } label: { label(for: key).frame(width: 130) }
                                .buttonStyle(ActionButtonStyle(prominent: key == "ok"))
                                .focused($focusedKey, equals: key)
                        }
                    }
                }
            }
        }
        .onExitCommand(perform: onCancel)
        .task {
            // The avatar that had focus is gone; without this nothing is focused until a swipe.
            try? await Task.sleep(for: .milliseconds(50))
            focusedKey = "1"
        }
    }

    @ViewBuilder
    private func label(for key: String) -> some View {
        switch key {
        case "delete": Image(systemName: "delete.left").accessibilityLabel("Delete")
        case "ok": Text("OK")
        default: Text(key).monospacedDigit()
        }
    }

    // Keys stay enabled throughout: tvOS doesn't reliably move focus onto a button that was disabled.
    private func press(_ key: String) {
        guard !checking else { return }
        error = nil
        switch key {
        case "delete":
            if pin.isEmpty { onCancel() } else { pin.removeLast() }
        case "ok":
            if pin.count < 4 { error = "A PIN is 4 to 8 digits." } else { submit() }
        default:
            guard pin.count < 8 else { return }
            pin.append(key)
        }
    }

    private func submit() {
        let entered = pin
        checking = true
        Task {
            await onSubmit(entered)
            checking = false
            // Only reached on a wrong PIN; a right one replaces this screen.
            pin = ""
            focusedKey = "1"
        }
    }
}
