import FlexarrKit
import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var status: SystemStatus?
    @State private var error: String?
    @State private var confirmSignOut = false
    @State private var playsFilesDirectly = UserDefaults.standard.bool(forKey: "FlexarrEngine")
    @State private var losslessAudio = UserDefaults.standard.bool(forKey: "FlexarrEngineLossless")

    var body: some View {
        HStack(alignment: .top, spacing: 100) {
            VStack(alignment: .leading, spacing: 16) {
                Text("Settings")
                    .font(.marquee(76, weight: .heavy))
                    .foregroundStyle(Color.screen)
                Text("Flexarr for Apple TV \(Bundle.main.shortVersion)")
                    .font(.system(size: 26))
                    .foregroundStyle(Color.usher)
            }
            .frame(width: 520, alignment: .leading)

            VStack(alignment: .leading, spacing: 40) {
                VStack(alignment: .leading, spacing: 0) {
                    Text("Server")
                        .font(.system(size: 26, weight: .semibold))
                        .foregroundStyle(Color.usher)
                        .padding(.bottom, 16)
                    row("Address", model.credentials?.address.absoluteString ?? "")
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
                .padding(36)
                .background(RoundedRectangle(cornerRadius: 18, style: .continuous).fill(Color.seat))

                VStack(alignment: .leading, spacing: 12) {
                    Text("Playback")
                        .font(.system(size: 26, weight: .semibold))
                        .foregroundStyle(Color.usher)
                    Button {
                        playsFilesDirectly.toggle()
                        UserDefaults.standard.set(playsFilesDirectly, forKey: "FlexarrEngine")
                    } label: {
                        Label(playsFilesDirectly ? "Play files directly: on" : "Play files directly: off", systemImage: "cpu")
                    }
                    .buttonStyle(ActionButtonStyle())
                    Text("Experimental. Plays the file itself on this Apple TV, every format with HDR and Dolby Vision as they are, instead of a stream from the server.")
                        .font(.system(size: 24))
                        .foregroundStyle(Color.usher)
                        .frame(maxWidth: 900, alignment: .leading)
                    if playsFilesDirectly {
                        Button {
                            losslessAudio.toggle()
                            UserDefaults.standard.set(losslessAudio, forKey: "FlexarrEngineLossless")
                        } label: {
                            Label(losslessAudio ? "Lossless surround: on" : "Lossless surround: off", systemImage: "hifispeaker.2")
                        }
                        .buttonStyle(ActionButtonStyle())
                        Text("TrueHD, DTS-HD and FLAC soundtracks as multichannel PCM over HDMI, for a receiver or eARC soundbar that takes it. Off, they're sent as Dolby Digital Plus, which anything plays.")
                            .font(.system(size: 24))
                            .foregroundStyle(Color.usher)
                            .frame(maxWidth: 900, alignment: .leading)
                    }
                }

                Button(role: .destructive) { confirmSignOut = true } label: {
                    Label("Sign out", systemImage: "rectangle.portrait.and.arrow.right")
                }
                .buttonStyle(ActionButtonStyle())
            }
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
        .padding(.vertical, 14)
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

