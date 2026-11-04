import FlexarrKit
import SwiftUI

/// Send diagnostics: this device's recent log, to the server only, for looking into a problem, with a note saying
/// what happened.
struct DiagnosticsSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var upload = DiagnosticsUpload()

    var body: some View {
        @Bindable var upload = upload
        List {
            Section {
                TextField(t("What happened, and roughly when? (optional)"), text: $upload.note, axis: .vertical)
                    .lineLimit(3...6)
                    .disabled(upload.isSending)
                    .listRowBackground(Color.seat)
            } footer: {
                Text(t("Sends what this {device} has logged lately to your server, and only there, so a problem can be looked into. Sign-ins and stream addresses are taken out first. (Error reports, when your server has them on, are separate: see Server.)", ["device": ThisDevice.name]))
            }

            Section {
                Button {
                    Task { await upload.send(model: model) }
                } label: {
                    HStack {
                        Text(upload.isSending ? t("Sending…") : t("Send diagnostics"))
                        Spacer()
                        if upload.isSending { ProgressView() }
                    }
                }
                .disabled(upload.isSending || model.client == nil)
                .listRowBackground(Color.seat)
            } footer: {
                if let outcome = upload.outcome { Text(outcome) }
            }
        }
        .settingsPage(t("Send diagnostics"))
    }
}
