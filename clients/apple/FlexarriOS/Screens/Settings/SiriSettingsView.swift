import FlexarrKit
import SwiftUI

struct SiriSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var devices: [Device] = []
    @State private var siriDevice = AppModel.siriDevice ?? ""

    var body: some View {
        List {
            Section {
                Picker(t("Siri plays on"), selection: $siriDevice) {
                    Text(t("This {device}", ["device": ThisDevice.name])).tag("")
                    ForEach(devices.filter { !$0.isThisOne }) { device in
                        Text(device.name).tag(device.id)
                    }
                    if !siriDevice.isEmpty, !devices.contains(where: { $0.id == siriDevice }) {
                        Text(AppModel.siriDeviceName ?? t("A device no longer signed in")).tag(siriDevice)
                    }
                }
                .settingsRow()
                .onChange(of: siriDevice) { _, id in
                    AppModel.siriDevice = id.isEmpty ? nil : id
                    AppModel.siriDeviceName = devices.first { $0.id == id }?.name
                }
            } footer: {
                Text(t("Choose an Apple TV and Siri plays there (Flexarr needs to be open on it). You can also name one as you ask."))
            }

            Section {
                ForEach([t("Play the next episode of Curb Your Enthusiasm in Flexarr"), t("Continue watching in Flexarr"), t("Pause Flexarr")], id: \.self) { phrase in
                    Text(t("“{phrase}”", ["phrase": phrase]))
                        .foregroundStyle(Color.usher)
                        .listRowBackground(Color.seat)
                }
            } header: {
                Text(t("Try saying"))
            } footer: {
                Text(t("Shows play from the episode you're up to."))
            }
        }
        .settingsPage("Siri")
        .task {
            if let loaded = try? await model.client?.devices() { devices = loaded }
        }
    }
}
