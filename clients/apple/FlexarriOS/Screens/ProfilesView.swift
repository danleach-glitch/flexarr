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
    /// Leaving a kids profile: the main profile's PIN is asked for, keeping the chosen profile's own if it had one.
    @State private var parent: ParentPin?
    /// The main profile's PIN, once it's been given right here (to unlock, or before a wrong PIN of the chosen
    /// profile's own): sent with the next choice, so it isn't asked for twice. Forgotten when the picker closes.
    @State private var knownParentPin: String?
    /// Locking this device to kids profiles, or unlocking it: the main profile's PIN is asked for.
    @State private var kidsLockPin: KidsLockStep?
    /// Lock to kids profiles is on its way to the server, so another tap doesn't send a second.
    @State private var locking = false
    @State private var switching: String?
    @State private var error: String?

    private struct ParentPin: Equatable {
        let ownPin: String?
    }

    private enum KidsLockStep { case lock, unlock }

    var body: some View {
        ZStack {
            RoomBackground(ambient: nil)
            if let step = kidsLockPin {
                PinEntry(profile: nil, title: "Enter the main profile’s PIN", error: $error, onCancel: { kidsLockPin = nil; error = nil }) { pin in
                    switch step {
                    case .unlock: await release(pin)
                    case .lock: await lock(pin: pin)
                    }
                }
                .transition(.opacity)
            } else if let asking {
                PinEntry(profile: parent == nil ? asking : model.profiles.first(where: \.isMain),
                         title: parent == nil ? nil : "Enter the main profile’s PIN", error: $error,
                         onCancel: { self.asking = nil; parent = nil; error = nil }) { pin in
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
        .animation(.easeInOut(duration: 0.2), value: asking)
        .animation(.easeInOut(duration: 0.2), value: kidsLockPin)
        // A link asked to play as a profile with a PIN: straight to its PIN.
        .onAppear { if let profile = model.linkProfile, !marksCurrent { asking = profile } }
        .onChange(of: model.linkProfile) { _, profile in if let profile, !marksCurrent { asking = profile } }
        .onDisappear { knownParentPin = nil }
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

                // Locked to kids profiles, the others wait behind the main profile's PIN.
                if model.kidsLock {
                    Button {
                        error = nil
                        kidsLockPin = .unlock
                    } label: {
                        Label("Unlock", systemImage: "lock.open")
                    }
                    .buttonStyle(ActionButtonStyle())
                } else if marksCurrent && model.offersKidsLock {
                    Button {
                        Task { await lock(pin: nil) }
                    } label: {
                        Label("Lock to kids profiles", systemImage: "lock")
                    }
                    .buttonStyle(ActionButtonStyle())
                    .disabled(locking || switching != nil)
                }
            }
            .frame(maxWidth: .infinity)
        }
    }

    private func caption(for profile: Profile) -> String? {
        if marksCurrent && profile.isCurrent { return "Watching now" }
        if profile.needsPinToChoose { return "PIN" }
        return nil
    }

    private func press(_ profile: Profile) async {
        error = nil
        if profile.needsPinToChoose {
            asking = profile
            return
        }
        await choose(profile, pin: nil)
    }

    /// `parentPin` is one just typed for the main profile; otherwise the one given earlier here goes, if any.
    private func choose(_ profile: Profile, pin: String?, parentPin: String? = nil) async {
        let sentParentPin = parentPin ?? knownParentPin
        switching = profile.id
        defer { switching = nil }
        do {
            try await model.switchProfile(to: profile, pin: pin, parentPin: sentParentPin)
            if marksCurrent { dismiss() }
        } catch FlexarrClientError.parentPinNeeded(let message) {
            if parentPin == nil {
                // Leaving a kids profile (or the PIN given earlier no longer works): ask for the main profile's PIN,
                // then try again with both.
                knownParentPin = nil
                asking = profile
                parent = ParentPin(ownPin: pin)
            } else {
                self.error = message ?? "That PIN isn’t right."
            }
        } catch FlexarrClientError.ownPinWrong {
            // The main profile's PIN was right, so it's kept; back to the chosen profile's own.
            knownParentPin = sentParentPin
            asking = profile
            parent = nil
            self.error = "That isn’t \(profile.name)’s PIN."
        } catch {
            self.error = ConnectionError(error).message
        }
    }

    private func release(_ pin: String) async {
        do {
            try await model.releaseKidsLock(pin: pin)
            knownParentPin = pin
            kidsLockPin = nil
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
            if marksCurrent { dismiss() }
        } catch FlexarrClientError.parentPinNeeded(let message) {
            if pin == nil { kidsLockPin = .lock } else { error = message ?? "That PIN isn’t right." }
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}

/// A 4 to 8 digit PIN, on the number pad: `profile`'s by default; with a `title`, whatever that says (the main
/// profile's, for a parent), under `profile`'s picture or, with none, a lock.
struct PinEntry: View {
    let profile: Profile?
    var title: String? = nil
    @Binding var error: String?
    let onCancel: () -> Void
    let onSubmit: (String) async -> Void

    @State private var pin = ""
    @State private var checking = false
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 22) {
            if let profile {
                ProfileAvatar(profile: profile, size: 84)
            } else {
                Image(systemName: "lock.circle.fill")
                    .font(.system(size: 72))
                    .foregroundStyle(Color.usher)
                    .frame(width: 84, height: 84)
            }
            Text(title ?? profile.map { "Enter \($0.name)’s PIN" } ?? "Enter the PIN")
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
                    // Not when the pad clears itself after a wrong PIN: the message saying so stays.
                    if !new.isEmpty { error = nil }
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
