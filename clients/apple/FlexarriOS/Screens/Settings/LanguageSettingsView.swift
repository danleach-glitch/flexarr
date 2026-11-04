import FlexarrKit
import SwiftUI

/// The profile's language, on every device; unset, each device's own.
struct LanguageSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var saving = false
    @State private var error: String?

    /// What the Settings row says: the language's own name, or Device language.
    static func current(_ model: AppModel) -> String {
        model.profileLanguage.flatMap { code in model.languages.first { $0.code == code }?.name } ?? t("Device language")
    }

    var body: some View {
        List {
            Section {
                row(t("Device language"), code: nil)
                ForEach(model.languages) { language in
                    row(language.name, code: language.code)
                }
            } footer: {
                if let error { Text(error).foregroundStyle(Color.danger) } else { Text(t("This profile’s own, on every device.")) }
            }
            .listRowBackground(Color.seat)
        }
        .settingsPage(t("Language"))
        .disabled(saving)
    }

    private func row(_ name: String, code: String?) -> some View {
        Button {
            Task {
                saving = true
                defer { saving = false }
                do { try await model.setLanguage(code) } catch { self.error = ConnectionError(error).message }
            }
        } label: {
            LabeledContent(name) {
                if model.profileLanguage == code { Image(systemName: "checkmark").foregroundStyle(Color.go) }
            }
        }
        .foregroundStyle(.primary)
    }
}
