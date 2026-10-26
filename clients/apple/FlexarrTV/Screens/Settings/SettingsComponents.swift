import FlexarrKit
import SwiftUI

/// A Settings page laid out the way the Apple TV's own Settings are: the page's name and, under it, what the focused
/// row does on the left; the rows on the right, so focus only ever moves up and down. Explanations live in the left
/// column rather than under each row, which keeps the list short enough to read at a glance.
struct SettingsPage<Rows: View>: View {
    let title: String
    /// Shown on the left when no row has focus yet, and under the page's title for pages that don't need more.
    var summary: String?
    @ViewBuilder var rows: () -> Rows

    @State private var focusedHelp: SettingHelp?

    var body: some View {
        HStack(alignment: .top, spacing: 90) {
            VStack(alignment: .leading, spacing: 34) {
                Text(title)
                    .font(.system(size: 58, weight: .semibold))
                    .foregroundStyle(Color.screen)
                    .accessibilityAddTraits(.isHeader)
                SettingHelpPanel(help: focusedHelp ?? summary.map { SettingHelp(text: $0) })
            }
            .frame(width: 560, alignment: .topLeading)

            // The focused row's lift must not be cut off at the column's edges.
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 10) {
                    rows()
                }
                .padding(.vertical, 24)
                .padding(.bottom, 60)
            }
            .scrollClipDisabled()
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, Layout.edge + 20)
        .padding(.top, 60)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ignoresSafeArea(edges: .horizontal)
        .background(RoomBackground(ambient: nil))
        .environment(\.showSettingHelp) { help in
            withAnimation(.easeOut(duration: 0.2)) { focusedHelp = help }
        }
    }
}

/// What the left column says about the focused row: an optional symbol in a tile, and a sentence or two.
struct SettingHelp: Hashable {
    var symbol: String?
    var text: String
}

private struct SettingHelpPanel: View {
    let help: SettingHelp?

    var body: some View {
        VStack(alignment: .leading, spacing: 30) {
            if let symbol = help?.symbol {
                Image(systemName: symbol)
                    .font(.system(size: 76, weight: .medium))
                    .foregroundStyle(Color.screen)
                    .frame(width: 168, height: 168)
                    .background(Color.aisle, in: .rect(cornerRadius: 36, style: .continuous))
                    .transition(.opacity)
            }
            if let text = help?.text, !text.isEmpty {
                Text(text)
                    .font(.system(size: 27))
                    .foregroundStyle(Color.usher)
                    .lineSpacing(4)
                    .fixedSize(horizontal: false, vertical: true)
                    .transition(.opacity)
            }
        }
        .id(help)
    }
}

private struct ShowSettingHelpKey: EnvironmentKey {
    static let defaultValue: @MainActor @Sendable (SettingHelp?) -> Void = { _ in }
}

extension EnvironmentValues {
    /// Set by the page: a row calls it as it gains focus, to put its explanation in the left column.
    var showSettingHelp: @MainActor @Sendable (SettingHelp?) -> Void {
        get { self[ShowSettingHelpKey.self] }
        set { self[ShowSettingHelpKey.self] = newValue }
    }
}

/// A group's name over its rows: small, dim and sentence case, as on the web and iPhone.
struct SettingsGroupLabel: View {
    let title: String

    init(_ title: String) { self.title = title }

    var body: some View {
        Text(title)
            .font(.system(size: 24, weight: .semibold))
            .foregroundStyle(Color.usher)
            .padding(.leading, 32)
            .padding(.top, 26)
            .padding(.bottom, 4)
    }
}

/// What sits at a row's right end.
enum SettingAccessory {
    case none
    /// Opens a page.
    case chevron
    /// The choice that's made, on a page of choices.
    case checkmark
}

/// One row: its name on the left, how it's set now on the right. Every kind of setting is this row, so a switch, a
/// choice and a page all look and move alike; pressing a switch's row flips it, as on the Apple TV's own Settings.
struct SettingsRow: View {
    let title: String
    var symbol: String?
    var value: String?
    var help: String?
    var accessory: SettingAccessory = .none
    var destructive = false
    let action: () -> Void

    @Environment(\.showSettingHelp) private var showHelp
    @FocusState private var focused: Bool

    var body: some View {
        Button(action: action) {
            HStack(spacing: 22) {
                if let symbol {
                    Image(systemName: symbol)
                        .font(.system(size: 26, weight: .semibold))
                        .frame(width: 40)
                }
                Text(title)
                    .lineLimit(1)
                Spacer(minLength: 30)
                if let value {
                    Text(value)
                        .lineLimit(1)
                        .opacity(0.7)
                }
                switch accessory {
                case .none: EmptyView()
                case .chevron:
                    Image(systemName: "chevron.right")
                        .font(.system(size: 24, weight: .semibold))
                        .opacity(0.55)
                case .checkmark:
                    Image(systemName: "checkmark")
                        .font(.system(size: 26, weight: .bold))
                }
            }
        }
        .buttonStyle(SettingsRowStyle(destructive: destructive))
        .focused($focused)
        .onChange(of: focused) { _, now in if now { showHelp(rowHelp) } }
        .onChange(of: rowHelp) { _, help in if focused { showHelp(help) } }
    }

    private var rowHelp: SettingHelp? {
        help.map { SettingHelp(symbol: symbol, text: $0) }
    }
}

extension SettingsRow {
    /// A switch: the row says On or Off and a press flips it.
    static func toggle(_ title: String, isOn: Bool, symbol: String? = nil, help: String? = nil, flip: @escaping () -> Void) -> SettingsRow {
        SettingsRow(title: title, symbol: symbol, value: isOn ? "On" : "Off", help: help, action: flip)
    }
}

/// A row that opens a page of its own.
struct SettingsLinkRow<Destination: View>: View {
    let title: String
    var symbol: String?
    var value: String?
    var help: String?
    @ViewBuilder var destination: () -> Destination

    @State private var open = false

    var body: some View {
        SettingsRow(title: title, symbol: symbol, value: value, help: help, accessory: .chevron) { open = true }
            .navigationDestination(isPresented: $open, destination: destination)
    }
}

/// A page of choices, for a setting with more than two: the chosen one has a tick, and choosing goes back.
struct SettingsChoicePage<Choice: Hashable>: View {
    let title: String
    var summary: String?
    let choices: [Choice]
    let label: (Choice) -> String
    let selection: Choice
    let choose: (Choice) -> Void

    @Environment(\.dismiss) private var dismiss
    @FocusState private var focused: Choice?

    var body: some View {
        SettingsPage(title: title, summary: summary) {
            ForEach(choices, id: \.self) { choice in
                SettingsRow(title: label(choice), accessory: choice == selection ? .checkmark : .none) {
                    choose(choice)
                    dismiss()
                }
                .focused($focused, equals: choice)
            }
        }
        // Opens on what's chosen now, so a press of Select leaves it as it was.
        .defaultFocus($focused, selection)
    }
}

/// Unfocused, a row is a quiet band on the room; focused it lifts in projected white, as the app's buttons do.
struct SettingsRowStyle: ButtonStyle {
    var destructive = false

    func makeBody(configuration: Configuration) -> some View {
        RowBody(configuration: configuration, destructive: destructive)
    }

    private struct RowBody: View {
        let configuration: Configuration
        let destructive: Bool
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .font(.system(size: 29, weight: .medium))
                .padding(.horizontal, 32)
                .frame(maxWidth: .infinity, minHeight: 78, alignment: .leading)
                .foregroundStyle(isFocused ? Color.room : (destructive ? Color.danger : Color.screen))
                .background {
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .fill(isFocused ? AnyShapeStyle(Color.screen) : AnyShapeStyle(Color.screen.opacity(0.07)))
                }
                .shadow(color: isFocused ? Color.black.opacity(0.4) : .clear, radius: 24, y: 10)
                .scaleEffect(configuration.isPressed ? 0.98 : (isFocused ? 1.03 : 1))
                .animation(.spring(response: 0.3, dampingFraction: 0.75), value: isFocused)
                .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
        }
    }
}

/// A fact on a page, not a control: the same band as a row, without focus.
struct SettingsFact: View {
    let label: String
    let value: String

    init(_ label: String, _ value: String) {
        self.label = label
        self.value = value
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 30) {
            Text(label)
                .foregroundStyle(Color.usher)
            Spacer(minLength: 30)
            Text(value)
                .foregroundStyle(Color.screen)
                .lineLimit(1)
                .truncationMode(.middle)
        }
        .font(.system(size: 29, weight: .medium))
        .padding(.horizontal, 32)
        .frame(maxWidth: .infinity, minHeight: 74, alignment: .leading)
    }
}
