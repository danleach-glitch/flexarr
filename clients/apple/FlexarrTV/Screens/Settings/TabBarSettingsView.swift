import SwiftUI

/// The order of the libraries and Live TV in the sidebar. Select one to pick it up, move it with up and down, and
/// select again to put it down, as when arranging apps on the Apple TV's Home screen.
struct TabBarSettingsView: View {
    @Environment(AppModel.self) private var model
    @AppStorage(TabBarEntry.storageKey) private var stored = ""
    /// The tab picked up, which moves where focus goes.
    @State private var moving: String?
    @FocusState private var focused: String?

    private var entries: [TabBarEntry] { TabBarEntry.ordered(model: model, stored: stored) }

    var body: some View {
        SettingsPage(title: "Sidebar", summary: "Home is always first, and Search and Settings last.") {
            SettingsGroupLabel("Between Home and Search")
            ForEach(entries) { entry in
                SettingsRow(title: entry.title, symbol: entry.symbol, value: moving == entry.id ? "Move up or down" : nil,
                            help: moving == entry.id ? "Press up or down to move \(entry.title), then select to put it down."
                                : "Select to pick up \(entry.title), then press up or down to move it.",
                            accessory: moving == entry.id ? .moving : .handle) {
                    moving = moving == entry.id ? nil : entry.id
                }
                .focused($focused, equals: entry.id)
            }
        }
        .onChange(of: focused) { old, new in
            guard let picked = moving else { return }
            // Focus going to the next row up or down while one is picked up moves it there instead; anywhere else
            // (the sidebar, Menu) puts it down where it is.
            guard old == picked, let new, let to = entries.firstIndex(where: { $0.id == new }) else {
                if new != picked { moving = nil }
                return
            }
            var order = entries
            guard let from = order.firstIndex(where: { $0.id == picked }) else { return }
            order.move(fromOffsets: [from], toOffset: to > from ? to + 1 : to)
            withAnimation(.spring(response: 0.3, dampingFraction: 0.85)) { stored = TabBarEntry.storing(order) }
            focused = picked
        }
        .onDisappear { moving = nil }
    }
}
