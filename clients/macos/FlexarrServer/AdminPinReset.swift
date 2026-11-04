import AppKit

/// Sets a new PIN for the main profile, which guards the server's settings, when it's been forgotten. In a browser
/// that takes the API key, which lives in the app's container where nobody would find it; from the menu bar, the app
/// sends the key itself.
@MainActor
struct AdminPinReset {
    let server: ServerProcess

    func run() async {
        guard let pin = askForPin() else { return }
        do {
            try await send(pin)
            tell(t("Flexarr has a new admin PIN"), t("Use it to unlock Flexarr’s settings. Anywhere the old one was given is locked again."))
        } catch {
            tell(t("Couldn’t set the PIN"), error.localizedDescription)
        }
    }

    /// The new PIN, typed twice; nil if cancelled.
    private func askForPin(problem: String? = nil) -> String? {
        let alert = NSAlert()
        alert.messageText = t("Reset Admin PIN")
        alert.informativeText = problem ?? t("Choose a new admin PIN: 4 to 8 digits. It’s asked for before Flexarr’s settings change. The main profile’s own PIN stays as it is.")
        alert.addButton(withTitle: t("Set PIN"))
        alert.addButton(withTitle: t(Words.cancel))
        let pin = NSSecureTextField(frame: NSRect(x: 0, y: 30, width: 220, height: 24))
        pin.placeholderString = t("New PIN")
        let again = NSSecureTextField(frame: NSRect(x: 0, y: 0, width: 220, height: 24))
        again.placeholderString = t("Confirm new PIN")
        let fields = NSView(frame: NSRect(x: 0, y: 0, width: 220, height: 54))
        fields.addSubview(pin)
        fields.addSubview(again)
        alert.accessoryView = fields
        alert.window.initialFirstResponder = pin
        NSApp.activate()
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }

        let typed = pin.stringValue
        guard (4...8).contains(typed.count), typed.allSatisfy(\.isASCII), typed.allSatisfy(\.isNumber) else {
            return askForPin(problem: t("A PIN is 4 to 8 digits. Try again."))
        }
        guard typed == again.stringValue else {
            return askForPin(problem: t("The two PINs don’t match. Type the same one in both."))
        }
        return typed
    }

    private func send(_ pin: String) async throws {
        let config = try JSONSerialization.jsonObject(with: Data(contentsOf: server.configFile)) as? [String: Any]
        guard let key = config?["apiKey"] as? String, !key.isEmpty else { throw ResetError(t("The server’s API key couldn’t be read.")) }
        var request = URLRequest(url: server.address.appending(path: "api/v1/admin/pin"))
        request.httpMethod = "POST"
        request.setValue(key, forHTTPHeaderField: "X-Api-Key")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["pin": pin])
        let (_, response) = try await URLSession.shared.data(for: request)
        guard let status = (response as? HTTPURLResponse)?.statusCode, (200..<300).contains(status) else {
            throw ResetError(t("Flexarr said no ({status}). Try Restart Server, then again.", ["status": "\((response as? HTTPURLResponse)?.statusCode ?? 0)"]))
        }
    }

    private func tell(_ title: String, _ detail: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = detail
        NSApp.activate()
        alert.runModal()
    }

    private struct ResetError: LocalizedError {
        let errorDescription: String?
        init(_ message: String) { errorDescription = message }
    }
}
