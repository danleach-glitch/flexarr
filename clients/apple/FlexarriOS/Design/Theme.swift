import FlexarrKit
import SwiftUI

// Phone and tablet sizes. The colour tokens, marquee type and room background are shared (Shared/Design/Colors.swift).

enum Layout {
    /// The shared left edge for heroes, rows and grids.
    static let edge: CGFloat = 20
    /// Posters in a row: a little over three across on a phone, more on an iPad.
    static func posterWidth(_ sizeClass: UserInterfaceSizeClass?) -> CGFloat { sizeClass == .regular ? 160 : 116 }
    static func wideWidth(_ sizeClass: UserInterfaceSizeClass?) -> CGFloat { sizeClass == .regular ? 340 : 260 }
    static let rowSpacing: CGFloat = 12
}

/// "2016   1h 56m   TV-MA": facts separated by space, never middle dots.
struct FactsLine: View {
    let facts: [String]

    var body: some View {
        HStack(spacing: 12) {
            ForEach(Array(facts.enumerated()), id: \.offset) { _, fact in
                Text(fact)
            }
        }
        .font(.subheadline.weight(.semibold))
        .foregroundStyle(Color.screen.opacity(0.85))
        .monospacedDigit()
        .lineLimit(1)
    }
}

/// Thin progress bar for resume points.
struct ProgressBar: View {
    let value: Double
    var tint: Color = .screen

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.black.opacity(0.45))
                Capsule().fill(tint).frame(width: max(4, proxy.size.width * value))
            }
        }
        .frame(height: 4)
    }
}

/// Section heading for rows: marquee type on the shared edge, with an optional See all (or another word).
struct SectionHeading: View {
    let title: String
    var actionTitle = "See all"
    var action: (() -> Void)?

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title)
                .font(.marquee(26, weight: .bold))
                .foregroundStyle(Color.screen)
            Spacer()
            if let action {
                Button(actionTitle, action: action)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(Color.usher)
            }
        }
        .padding(.horizontal, Layout.edge)
    }
}

/// Play and the other actions on heroes and details: the main one filled with the title's ambient colour (see
/// `Color.mainButton`), the rest quieter on a translucent seat.
struct ActionButtonStyle: ButtonStyle {
    /// The title's ambient colour, for the main button's fill; nil gives silver.
    var ambient: Color? = nil
    var prominent = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.body.weight(.semibold))
            .labelStyle(ActionLabelStyle())
            .padding(.horizontal, 20)
            .frame(minHeight: 48)
            .foregroundStyle(prominent ? Color.room : Color.screen)
            .background {
                RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                    .fill(prominent ? AnyShapeStyle(Color.mainButton(ambient)) : AnyShapeStyle(Color.screen.opacity(0.14)))
            }
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
            .opacity(configuration.isPressed ? 0.85 : 1)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }

    private struct ActionLabelStyle: LabelStyle {
        func makeBody(configuration: Configuration) -> some View {
            HStack(spacing: 8) {
                configuration.icon.font(.body.weight(.bold))
                configuration.title
            }
        }
    }
}

/// A round icon-only action beside Play (Start over, Mark watched).
struct RoundActionStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .labelStyle(.iconOnly)
            .font(.body.weight(.semibold))
            .foregroundStyle(Color.screen)
            .frame(width: 48, height: 48)
            .background(Circle().fill(Color.screen.opacity(0.14)))
            .scaleEffect(configuration.isPressed ? 0.94 : 1)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }
}

/// A round action with its name underneath (the row under Play on a title's page).
struct LabeledActionStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .labelStyle(Stacked())
            .scaleEffect(configuration.isPressed ? 0.94 : 1)
            .opacity(configuration.isPressed ? 0.85 : 1)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }

    private struct Stacked: LabelStyle {
        func makeBody(configuration: Configuration) -> some View {
            VStack(spacing: 6) {
                configuration.icon
                    .font(.body.weight(.semibold))
                    .foregroundStyle(Color.screen)
                    .frame(width: 48, height: 48)
                    .background(Circle().fill(Color.screen.opacity(0.14)))
                configuration.title
                    .font(.caption.weight(.medium))
                    .foregroundStyle(Color.usher)
                    .multilineTextAlignment(.center)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
        }
    }
}

/// Small selectable pill for seasons and sorting.
struct PillButtonStyle: ButtonStyle {
    var selected: Bool
    var ambient: Color = .silver

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.subheadline.weight(selected ? .bold : .medium))
            .padding(.horizontal, 16)
            .frame(height: 36)
            .foregroundStyle(selected ? Color.screen : Color.usher)
            .background {
                Capsule().fill(selected ? ambient.opacity(0.32) : Color.clear)
            }
            .overlay {
                if !selected { Capsule().strokeBorder(Color.aisle, lineWidth: 1.5) }
            }
            .opacity(configuration.isPressed ? 0.7 : 1)
    }
}

/// Loading, error and empty states in plain words.
struct StatusView: View {
    /// Offline, a failure is just that: the Offline bar says the rest, and the screen loads again once it's back.
    @Environment(AppModel.self) private var model: AppModel?

    enum State {
        case loading
        case failed(String, retry: () -> Void)
        case empty(String)
    }

    let state: State

    var body: some View {
        VStack(spacing: 16) {
            switch state {
            case .loading:
                ProgressView().controlSize(.large)
            case .failed where model?.isOffline == true:
                Image(systemName: "wifi.slash").font(.system(size: 40)).foregroundStyle(Color.usher).accessibilityHidden(true)
                Text("You’re offline")
                    .font(.body.weight(.medium))
                    .foregroundStyle(Color.screen)
            case .failed(let message, let retry):
                Image(systemName: "wifi.exclamationmark").font(.system(size: 40)).foregroundStyle(Color.usher)
                Text(message)
                    .font(.body.weight(.medium))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                Button("Try again", action: retry)
                    .buttonStyle(ActionButtonStyle(prominent: true))
            case .empty(let message):
                Text(message)
                    .font(.body.weight(.medium))
                    .foregroundStyle(Color.usher)
                    .multilineTextAlignment(.center)
            }
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// A switch that's green when on, whatever the surrounding tint (the app's is silver).
struct GoSwitchStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        Toggle(configuration).toggleStyle(.switch).tint(Color.go)
    }
}
