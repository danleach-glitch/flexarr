import FlexarrKit
import SwiftUI

struct SiriSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var devices: [Device] = []
    @State private var siriDevice = AppModel.siriDevice ?? ""

    var body: some View {
        List {
            Section {
                Picker("Siri plays on", selection: $siriDevice) {
                    Text("This \(ThisDevice.name)").tag("")
                    ForEach(devices.filter { !$0.isThisOne }) { device in
                        Text(device.name).tag(device.id)
                    }
                    if !siriDevice.isEmpty, !devices.contains(where: { $0.id == siriDevice }) {
                        Text(AppModel.siriDeviceName ?? "A device no longer signed in").tag(siriDevice)
                    }
                }
                .settingsRow()
                .onChange(of: siriDevice) { _, id in
                    AppModel.siriDevice = id.isEmpty ? nil : id
                    AppModel.siriDeviceName = devices.first { $0.id == id }?.name
                }
            } footer: {
                Text("Choose an Apple TV and Siri plays there (Flexarr needs to be open on it). You can also name one as you ask.")
            }

            Section {
                ForEach(["Play the next episode of Curb Your Enthusiasm in Flexarr", "Continue watching in Flexarr", "Pause Flexarr"], id: \.self) { phrase in
                    Text("\u{201C}\(phrase)\u{201D}")
                        .foregroundStyle(Color.usher)
                        .listRowBackground(Color.seat)
                }
            } header: {
                Text("Try saying")
            } footer: {
                Text("Shows play from the episode you're up to.")
            }
        }
        .settingsPage("Siri")
        .task {
            if let loaded = try? await model.client?.devices() { devices = loaded }
        }
    }
}
