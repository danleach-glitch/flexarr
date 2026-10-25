import FlexarrKit
import SwiftUI

/// Settings: the page title and what's installed on the left; on the right a card for the server, a card for
/// playback choices and Sign out on its own at the end, so focus walks straight down from the first control to the last.
struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var status: SystemStatus?
    @State private var error: String?
    @State private var confirmSignOut = false
    @State private var playsFilesDirectly = PlaybackController.usesEngine
    @State private var losslessAudio = UserDefaults.standard.bool(forKey: "FlexarrEngineLossless")

    var body: some View {
        HStack(alignment: .top, spacing: 100) {
            VStack(alignment: .leading, spacing: 16) {
                Text("Settings")
                    .font(.marquee(76, weight: .heavy))
                    .foregroundStyle(Color.screen)
                VStack(alignment: .leading, spacing: 6) {
                    Text("Flexarr for Apple TV")
                    Text("Version \(Bundle.main.shortVersion)")
                }
                .font(.system(size: 26))
                .foregroundStyle(Color.usher)
            }
            .frame(width: 520, alignment: .leading)

            // The cards outgrow the screen once Lossless surround shows, so the column scrolls; the focused
            // button's lift and glow must not be cut off at the column's edges.
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 40) {
                    card("Server") {
                        row("Address", model.credentials?.address.absoluteString ?? "")
                        if let remote = model.credentials?.remoteAddress {
                            row("Away from home", remote.absoluteString)
                        }
                        if let status {
                            row("Version", ServerVersion.short(status.version))
                            row("Running on", status.osDescription)
                            row("Runtime", status.runtimeVersion)
                            row("Up since", status.startTime.formatted(date: .abbreviated, time: .shortened))
                        } else if let error {
                            row("Status", error)
                        } else {
                            row("Status", "Checking")
                        }
                        row("Libraries", model.libraries.map(\.name).joined(separator: ", "))
                    }

                    card("Playback") {
                        VStack(alignment: .leading, spacing: 32) {
                            setting("Files play on this Apple TV as they are, every format with HDR and Dolby Vision. Off, the server sends a stream instead.") {
                                Button {
                                    playsFilesDirectly.toggle()
                                    PlaybackController.usesEngine = playsFilesDirectly
                                } label: {
                                    Label(playsFilesDirectly ? "Play files directly: on" : "Play files directly: off", systemImage: "cpu")
                                }
                            }
                            if playsFilesDirectly {
                                setting("TrueHD, DTS-HD and FLAC as multichannel PCM over HDMI, for a receiver or eARC soundbar that takes it. Off, they go as Dolby Digital Plus, which anything plays.") {
                                    Button {
                                        losslessAudio.toggle()
                                        UserDefaults.standard.set(losslessAudio, forKey: "FlexarrEngineLossless")
                                    } label: {
                                        Label(losslessAudio ? "Lossless surround: on" : "Lossless surround: off", systemImage: "hifispeaker.2")
                                    }
                                }
                            }
                        }
                        .padding(.top, 8)
                    }

                    Button(role: .destructive) { confirmSignOut = true } label: {
                        Label("Sign out", systemImage: "rectangle.portrait.and.arrow.right")
                    }
                    .buttonStyle(ActionButtonStyle())
                    .padding(.top, 8)
                }
                .padding(.bottom, 60)
            }
            .scrollClipDisabled()
            .frame(maxWidth: 1000, alignment: .leading)
        }
        .padding(.horizontal, Layout.edge)
        .padding(.top, 40)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ignoresSafeArea(edges: .horizontal)
        .background(RoomBackground(ambient: nil))
        .task { await load() }
        .confirmationDialog("Sign out of this server?", isPresented: $confirmSignOut, titleVisibility: .visible) {
            Button("Sign out", role: .destructive) { model.signOut() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This Apple TV will need to connect again to use the server.")
        }
    }

    /// A titled card on a seat: the Server and Playback groups share its padding and background.
    private func card<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(title)
                .font(.system(size: 26, weight: .semibold))
                .foregroundStyle(Color.usher)
                .padding(.bottom, 16)
            content()
        }
        .padding(36)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 18, style: .continuous).fill(Color.seat))
    }

    /// A control with its explanation directly beneath it, so what a button does is read where it's pressed.
    private func setting<Control: View>(_ explanation: String, @ViewBuilder control: () -> Control) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            control()
                .buttonStyle(ActionButtonStyle())
            Text(explanation)
                .font(.system(size: 24))
                .foregroundStyle(Color.usher)
                .frame(maxWidth: 860, alignment: .leading)
        }
    }

    private func row(_ label: String, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline) {
            Text(label)
                .foregroundStyle(Color.usher)
                .frame(width: 240, alignment: .leading)
            Text(value)
                .foregroundStyle(Color.screen)
                .lineLimit(2)
        }
        .font(.system(size: 27))
        .padding(.vertical, 12)
    }

    private func load() async {
        guard let client = model.client else { return }
        do {
            status = try await client.systemStatus()
        } catch {
            self.error = ConnectionError(error).message
        }
    }
}
