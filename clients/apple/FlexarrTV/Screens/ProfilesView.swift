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
    /// Leaving a kids profile: the pad asks for the admin PIN, keeping the chosen profile's own if it had one.
    @State private var parent: ParentPin?
    /// The admin PIN, once it's been given right here (to unlock, or before a wrong PIN of the chosen
    /// profile's own): sent with the next choice, so it isn't asked for twice. Forgotten when the picker closes.
    @State private var knownParentPin: String?
    /// Locking this Apple TV to kids profiles, or unlocking it: the pad asks for the admin PIN.
    @State private var kidsLockPin: KidsLockStep?
    /// Lock to kids profiles is on its way to the server, so another press doesn't send a second.
    @State private var locking = false
    @State private var switching: String?
    @State private var error: String?
    @FocusState private var focused: String?

    private struct ParentPin: Equatable {
        let ownPin: String?
    }

    private enum KidsLockStep { case lock, unlock }

    /// Focus ids for the buttons under the profiles (profiles go by their ids).
    /// The kids profile held: child lock goes on with it.
    @State private var lockTarget: Profile?

    var body: some View {
        VStack(spacing: 70) {
            if let step = kidsLockPin {
                PinPad(profile: nil, title: "Enter the admin PIN", error: $error, onCancel: cancelKidsLockPin) { pin in
                    switch step {
                    case .unlock: await release(pin)
                    case .lock: await lock(pin: pin)
                    }
                }
                .transition(.opacity)
            } else if let asking {
                PinPad(profile: parent == nil ? asking : mainProfile, title: parent == nil ? nil : "Enter the admin PIN", error: $error,
                       onCancel: { cancelPin(asking) }) { pin in
                    if let parent {
                        await choose(asking, pin: parent.ownPin, parentPin: pin)
                    } else {
                        await choose(asking, pin: pin)
                    }
                }
                .transition(.opacity)
            } else {
                choices.transition(.opacity)
            }
        }
        .animation(.easeInOut(duration: 0.25), value: asking)
        .animation(.easeInOut(duration: 0.25), value: kidsLockPin)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoomBackground(ambient: nil))
        // A link asked to play as a profile with a PIN: straight to its PIN.
        .onAppear { if let profile = model.linkProfile, !marksCurrent { asking = profile } }
        .onChange(of: model.linkProfile) { _, profile in if let profile, !marksCurrent { asking = profile } }
        .onDisappear { knownParentPin = nil }
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
                                      busy: switching == profile.id || (locking && lockTarget?.id == profile.id), isFocused: focused == profile.id,
                                      padlock: lockBadge(for: profile) ? model.kidsLock : nil)
                    }
                    .buttonStyle(ProfileButtonStyle())
                    .focused($focused, equals: profile.id)
                    // Press and hold, as holding the padlock on the phones: child lock on with this kids profile, or off.
                    .contextMenu {
                        if lockBadge(for: profile) {
                            Button(model.kidsLock ? "Turn off child lock" : "Child lock", systemImage: model.kidsLock ? "lock.open" : "lock") {
                                toggleChildLock(on: profile)
                            }
                        }
                    }
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

    private var mainProfile: Profile? { model.profiles.first(where: \.isMain) }

    /// The padlock over each kids profile: open while child lock can be turned on (it locks to that profile), closed
    /// while it's on. As on the phones.
    private func lockBadge(for profile: Profile) -> Bool {
        guard profile.isKidsProfile else { return false }
        // In the picker at launch too, not only when it's opened to switch.
        return model.kidsLock || model.offersKidsLock
    }

    private func toggleChildLock(on profile: Profile) {
        guard switching == nil, !locking else { return }
        error = nil
        if model.kidsLock {
            kidsLockPin = .unlock
        } else {
            lockTarget = profile
            Task { await lock(pin: nil) }
        }
    }

    private var defaultFocus: String? {
        guard marksCurrent else { return model.profiles.first?.id }
        return (model.otherProfile ?? model.profiles.first(where: { !$0.isCurrent }))?.id
    }

    private func caption(for profile: Profile) -> String? {
        if marksCurrent && profile.isCurrent { return "Watching now" }
        if profile.needsPinToChoose { return "PIN" }
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
        if profile.needsPinToChoose {
            // The admin PIN was just given here (child lock off): it goes with this choice, rather than the
            // keypad again. A profile with a PIN of its own (other than the main profile) still asks for that.
            if let held = knownParentPin ?? model.recentParentPin, profile.isMain || !profile.hasPin {
                await choose(profile, pin: profile.isMain ? held : nil, parentPin: held)
                return
            }
            asking = profile
            return
        }
        await choose(profile, pin: nil)
    }

    private func cancelPin(_ profile: Profile) {
        asking = nil
        parent = nil
        error = nil
        // The avatar was replaced by the keypad, so focus has to be handed back.
        Task {
            try? await Task.sleep(for: .milliseconds(50))
            focused = profile.id
        }
    }

    /// `parentPin` is one just typed on the main profile's pad; otherwise the one given earlier here goes, if any.
    private func choose(_ profile: Profile, pin: String?, parentPin: String? = nil) async {
        let sentParentPin = parentPin ?? knownParentPin
        switching = profile.id
        defer { switching = nil }
        do {
            try await model.switchProfile(to: profile, pin: pin, parentPin: sentParentPin)
        } catch FlexarrClientError.parentPinNeeded(let message) {
            if parentPin == nil {
                // Leaving a kids profile (or the PIN given earlier no longer works): ask for the admin PIN,
                // then try again with both.
                knownParentPin = nil
                asking = profile
                parent = ParentPin(ownPin: pin)
            } else {
                self.error = message ?? "That PIN isn’t right."
            }
        } catch FlexarrClientError.ownPinWrong {
            // The admin PIN was right, so it's kept; back to the chosen profile's own.
            knownParentPin = sentParentPin
            asking = profile
            parent = nil
            self.error = "That isn’t \(profile.name)’s PIN."
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    /// The pad replaced the button, so focus has to be handed back: to it, or once unlocked (when it's gone) to the
    /// first profile.
    private func cancelKidsLockPin() {
        let step = kidsLockPin
        kidsLockPin = nil
        error = nil
        Task {
            try? await Task.sleep(for: .milliseconds(50))
            switch step {
            case .unlock: focused = model.profiles.first(where: \.isKidsProfile)?.id ?? model.profiles.first?.id
            case .lock: focused = lockTarget?.id ?? model.profiles.first(where: \.isKidsProfile)?.id
            case nil: break
            }
        }
    }

    private func release(_ pin: String) async {
        do {
            try await model.releaseKidsLock(pin: pin)
            knownParentPin = pin
            cancelKidsLockPin()
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    /// Without a PIN first: the main profile, unlocked here, needs none; otherwise the server asks for it.
    private func lock(pin: String?) async {
        guard !locking else { return }
        locking = true
        defer { locking = false }
        error = nil
        do {
            try await model.lockToKids(pin: pin)
            kidsLockPin = nil
            // The server chose a kids profile: the one held, if it's another.
            if let target = lockTarget, model.currentProfile?.id != target.id {
                try? await model.switchProfile(to: target)
            }
            lockTarget = nil
        } catch FlexarrClientError.parentPinNeeded(let message) {
            if pin == nil { kidsLockPin = .lock } else { error = message ?? "That PIN isn’t right." }
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
    /// Child lock's padlock over a kids profile: closed (true) while it's on, open (false) while it can be; nil, none.
    var padlock: Bool? = nil

    var body: some View {
        VStack(spacing: 28) {
            ProfileAvatar(profile: profile)
                .overlay {
                    if busy {
                        Circle().fill(Color.black.opacity(0.45))
                        ProgressView()
                    } else if let closed = padlock {
                        Circle().fill(Color.black.opacity(0.35))
                        Image(systemName: closed ? "lock.fill" : "lock.open.fill")
                            .font(.system(size: 64, weight: .bold))
                            .foregroundStyle(Color.screen)
                            .shadow(color: .black.opacity(0.4), radius: 6)
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

/// A 4 to 8 digit PIN entered with the remote: a keypad, since the Siri Remote has no number keys. `profile`'s by
/// default; with a `title`, whatever that says (the main profile's, for a parent), under `profile`'s picture or, with
/// none, a lock.
struct PinPad: View {
    let profile: Profile?
    var title: String? = nil
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
                if let profile {
                    ProfileAvatar(profile: profile, size: 180)
                } else {
                    Image(systemName: "lock.circle.fill")
                        .font(.system(size: 150))
                        .foregroundStyle(Color.usher)
                        .frame(width: 180, height: 180)
                }
                Text(title ?? profile.map { "Enter \($0.name)’s PIN" } ?? "Enter the PIN")
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
