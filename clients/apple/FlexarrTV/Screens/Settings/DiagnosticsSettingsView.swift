import FlexarrKit
import SwiftUI

/// Send diagnostics: this Apple TV's recent log, to the server only, for looking into a problem. A note says what
/// happened; the line under the button says how the send went.
struct DiagnosticsSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var upload = DiagnosticsUpload()
    @State private var editingNote = false
    @State private var draft = ""

    var body: some View {
        SettingsPage(title: "Send diagnostics",
                     summary: "Sends what this Apple TV has logged lately to your server, and nowhere else, so a problem can be looked into there. Sign-ins and stream addresses are taken out first.") {
            SettingsRow(title: "Note", symbol: "square.and.pencil", value: upload.note.isEmpty ? "Optional" : upload.note,
                        help: "What happened and roughly when, so it's easy to find in the log.") {
                draft = upload.note
                editingNote = true
            }
            .disabled(upload.isSending)
            SettingsRow(title: upload.isSending ? "Sending…" : "Send", symbol: "paperplane",
                        help: "Send the log now, with the note.") {
                Task { await upload.send(model: model) }
            }
            .disabled(upload.isSending)
            if upload.isSending {
                ProgressView()
                    .padding(.leading, 32)
                    .padding(.top, 20)
            } else if let outcome = upload.outcome {
                Text(outcome)
                    .font(.system(size: 27))
                    .foregroundStyle(Color.usher)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 32)
                    .padding(.top, 20)
            }
        }
        .alert("Note", isPresented: $editingNote) {
            TextField("What happened?", text: $draft)
            Button("Done") { upload.note = draft }
            Button("Cancel", role: .cancel) {}
        }
    }
}
