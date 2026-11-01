import FlexarrKit
import SwiftUI

/// How this profile listens to books, on every device: the speed for books without their own, the skip steps and
/// smart rewind (the same settings as the web's Settings › Audiobooks).
struct AudiobookSettingsView: View {
    @Environment(AppModel.self) private var model
    @State private var failed = false

    /// The web's choices, plus the profile's own when it's another.
    private static let presets: [Double] = [0.8, 1, 1.1, 1.2, 1.25, 1.5, 1.75, 2, 2.5, 3]

    var body: some View {
        List {
            if let listening = model.bookListening {
                Section {
                    Picker("Speed", selection: binding(\.speed, listening) { BookListeningUpdate(speed: $0) }) {
                        ForEach(speeds(listening.speed), id: \.self) { Text(BookSpeed.label($0)).tag($0) }
                    }
                    .listRowBackground(Color.seat)
                    Picker("Skip back", selection: binding(\.skipBackSeconds, listening) { BookListeningUpdate(skipBackSeconds: $0) }) {
                        ForEach(steps(listening.skipBackSeconds), id: \.self) { Text("\($0) seconds").tag($0) }
                    }
                    .listRowBackground(Color.seat)
                    Picker("Skip forward", selection: binding(\.skipForwardSeconds, listening) { BookListeningUpdate(skipForwardSeconds: $0) }) {
                        ForEach(steps(listening.skipForwardSeconds), id: \.self) { Text("\($0) seconds").tag($0) }
                    }
                    .listRowBackground(Color.seat)
                    Toggle("Smart rewind", isOn: binding(\.smartRewind, listening) { BookListeningUpdate(smartRewind: $0) })
                        .settingsRow()
                } header: {
                    Text("Your profile, on every device")
                } footer: {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Speed is for books without one of their own. Smart rewind starts a little earlier after a pause.")
                        if failed { Text("That didn't save. Try again.").foregroundStyle(Color.danger) }
                    }
                }
            } else {
                StatusView(state: .loading).listRowBackground(Color.clear)
            }
        }
        .settingsPage("Audiobooks")
        .task { if model.bookListening == nil { await model.loadBookListening() } }
    }

    private func binding<Value>(_ path: KeyPath<BookListening, Value>, _ listening: BookListening,
                                update: @escaping (Value) -> BookListeningUpdate) -> Binding<Value> {
        Binding(get: { model.bookListening?[keyPath: path] ?? listening[keyPath: path] }, set: { value in
            Task { failed = !(await model.updateBookListening(update(value))) }
        })
    }

    private func speeds(_ current: Double) -> [Double] {
        Self.presets.contains(current) ? Self.presets : (Self.presets + [current]).sorted()
    }

    private func steps(_ current: Int) -> [Int] {
        BookListening.skipSteps.contains(current) ? BookListening.skipSteps : (BookListening.skipSteps + [current]).sorted()
    }
}
