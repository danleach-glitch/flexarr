import FlexarrKit
import SwiftUI

/// The drop-down under the profile picture in the corner: who's watching, Switch profile and Settings. Presented as
/// a cover so it's modal (focus stays in it and Menu closes it), drawn as a panel under the picture over the dimmed
/// screen. Settings and the profile picker open on top of it, so Menu from them comes back here.
struct ProfileMenuView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var shown = false
    @State private var choosingProfile = false
    @State private var showingSettings = false
    @FocusState private var focused: Item?

    private enum Item: Hashable { case switchProfile, settings }

    var body: some View {
        ZStack(alignment: .topTrailing) {
            Color.black.opacity(shown ? 0.4 : 0).ignoresSafeArea()
            VStack(alignment: .leading, spacing: 10) {
                if let profile = model.currentProfile {
                    HStack(spacing: 18) {
                        ProfileAvatar(profile: profile, size: 64)
                        Text(profile.name)
                            .font(.system(size: 30, weight: .bold))
                            .foregroundStyle(Color.screen)
                            .lineLimit(1)
                    }
                    .padding(.horizontal, 32)
                    .padding(.top, 28)
                    .padding(.bottom, 14)
                }
                if model.profiles.count > 1 {
                    row("Switch profile", systemImage: "person.2") { choosingProfile = true }
                        .focused($focused, equals: .switchProfile)
                }
                row("Settings", systemImage: "gearshape") { showingSettings = true }
                    .focused($focused, equals: .settings)
            }
            .padding([.horizontal, .bottom], 20)
            .frame(width: 480)
            .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 30, style: .continuous))
            .shadow(color: .black.opacity(0.35), radius: 30, y: 12)
            .padding(.trailing, Layout.edge - 12)
            // Just under the tab bar, which sits 46pt from the top and is 68pt tall.
            .padding(.top, 46 + 68 + 14)
            .ignoresSafeArea(edges: .top)
            .opacity(shown ? 1 : 0)
            .scaleEffect(shown ? 1 : 0.94, anchor: .topTrailing)
            .focusSection()
        }
        .presentationBackground(.clear)
        .onAppear {
            withAnimation(.easeOut(duration: 0.22)) { shown = true }
            focused = model.profiles.count > 1 ? .switchProfile : .settings
        }
        .onExitCommand { dismiss() }
        .fullScreenCover(isPresented: $choosingProfile) { ProfilePickerView(marksCurrent: true) }
        .fullScreenCover(isPresented: $showingSettings) { TabStack { SettingsView() } }
        .onChange(of: model.currentProfile?.id) { _, _ in
            // Switched: the picker closes itself; this closes a beat later, after that animation.
            Task {
                try? await Task.sleep(for: .milliseconds(450))
                dismiss()
            }
        }
    }

    private func row(_ title: String, systemImage: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .buttonStyle(ActionButtonStyle())
        .accessibilityIdentifier("profile-menu-\(systemImage)")
    }
}
