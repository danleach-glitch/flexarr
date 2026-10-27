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
                TextField("What happened, and roughly when? (optional)", text: $upload.note, axis: .vertical)
                    .lineLimit(3...6)
                    .disabled(upload.isSending)
                    .listRowBackground(Color.seat)
            } footer: {
                Text("Sends what this \(ThisDevice.name) has logged lately to your server, and nowhere else, so a problem can be looked into there. Sign-ins and stream addresses are taken out first.")
            }

            Section {
                Button {
                    Task { await upload.send(model: model) }
                } label: {
                    HStack {
                        Text(upload.isSending ? "Sending…" : "Send diagnostics")
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
        .settingsPage("Send diagnostics")
    }
}
