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
        SettingsPage(title: t("Language")) {
            SettingsRow(title: t("Device language"), symbol: "globe", help: error ?? t("This profile’s own, on every device."),
                        accessory: model.profileLanguage == nil ? .checkmark : .none) { choose(nil) }
            ForEach(model.languages) { language in
                SettingsRow(title: language.name, symbol: "character.bubble",
                            accessory: model.profileLanguage == language.code ? .checkmark : .none) { choose(language.code) }
            }
        }
    }

    private func choose(_ code: String?) {
        // Not disabled while it's busy: tvOS doesn't reliably move focus back onto a button that was.
        guard !saving else { return }
        Task {
            saving = true
            defer { saving = false }
            error = nil
            do { try await model.setLanguage(code) } catch { self.error = ConnectionError(error).message }
        }
    }
}
