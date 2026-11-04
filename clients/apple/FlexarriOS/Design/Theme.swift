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

/// "2016   1h 56m   TV-MA": facts separated by space, never middle dots, then any scores beside their marks. When
/// they don't all fit on one line (a narrow phone, large text), the scores go on a line of their own rather than
/// cutting a fact short.
struct FactsLine: View {
    let facts: [String]
    var scores: Scores? = nil
    /// The marks grow with the text.
    @ScaledMetric(relativeTo: .subheadline) private var markHeight: CGFloat = 15

    var body: some View {
        Group {
            if let scores, !scores.isEmpty {
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 12) {
                        factTexts
                        ScoreChips(scores: scores, markHeight: markHeight, spacing: 12)
                    }
                    VStack(alignment: .leading, spacing: 6) {
                        HStack(spacing: 12) { factTexts }
                        ScoreChips(scores: scores, markHeight: markHeight, spacing: 12)
                    }
                }
            } else {
                HStack(spacing: 12) { factTexts }
            }
        }
        .font(.subheadline.weight(.semibold))
        .foregroundStyle(Color.screen.opacity(0.85))
        .monospacedDigit()
        .lineLimit(1)
    }

    private var factTexts: some View {
        ForEach(Array(facts.enumerated()), id: \.offset) { _, fact in
            Text(fact)
        }
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
    /// What VoiceOver says for the action, when "See all" alone wouldn't say what of.
    var actionLabel: String?

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
                    .accessibilityLabel(actionLabel ?? actionTitle)
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
    /// Keeps the label on one line at its full width, for a short word in a row of round buttons (the music action
    /// row's Play). Off elsewhere, so a longer label wraps rather than run off the screen at large text sizes.
    var singleLine = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.body.weight(.semibold))
            .labelStyle(ActionLabelStyle(singleLine: singleLine))
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
        let singleLine: Bool

        func makeBody(configuration: Configuration) -> some View {
            HStack(spacing: 8) {
                configuration.icon.font(.body.weight(.bold))
                configuration.title.lineLimit(singleLine ? 1 : nil)
            }
            .fixedSize(horizontal: singleLine, vertical: false)
        }
    }
}

/// A round icon-only action beside Play (Start over, Mark watched, Shuffle, Follow). `selected` shows it on (Following,
/// Requested) in the page's ambient tint, as a selected pill is; disabled, it dims. `labelled` spells it out as a pill
/// in the same on and off looks, for a page where it's the only button (nothing to play beside it). The circle grows
/// with the text size, except where `scales` is off (a bar of a fixed height).
struct RoundActionStyle: ButtonStyle {
    var selected = false
    var ambient: Color = .silver
    var labelled = false
    var scales = true

    func makeBody(configuration: Configuration) -> some View {
        RoundBody(configuration: configuration, selected: selected, ambient: ambient, labelled: labelled, scales: scales)
    }

    private struct RoundBody: View {
        let configuration: Configuration
        let selected: Bool
        let ambient: Color
        let labelled: Bool
        let scales: Bool
        @Environment(\.isEnabled) private var isEnabled
        @ScaledMetric(relativeTo: .body) private var scaledSize: CGFloat = 48

        private var size: CGFloat { scales ? scaledSize : 48 }

        var body: some View {
            Group {
                if labelled {
                    HStack(spacing: 8) {
                        configuration.label.labelStyle(.iconOnly).font(.body.weight(.bold))
                        configuration.label.labelStyle(.titleOnly)
                    }
                    .padding(.horizontal, 20)
                    .frame(minHeight: size)
                } else {
                    configuration.label
                        .labelStyle(.iconOnly)
                        .frame(width: size, height: size)
                }
            }
            .font(.body.weight(.semibold))
            .foregroundStyle(Color.screen)
            .background(shape.fill(selected ? AnyShapeStyle(ambient.opacity(0.45)) : AnyShapeStyle(Color.screen.opacity(0.14))))
            .overlay { if selected { shape.strokeBorder(ambient.opacity(0.8), lineWidth: 1.5) } }
            .opacity(isEnabled ? 1 : 0.4)
            .scaleEffect(configuration.isPressed ? 0.94 : 1)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
        }

        /// A circle, or the pill's rounded rectangle (as `ActionButtonStyle`'s).
        private var shape: some InsettableShape {
            RoundedRectangle(cornerRadius: labelled ? Radius.control : size / 2, style: .continuous)
        }
    }
}

/// A page's action buttons (Play, then the round ones), centred in a row. When they don't all fit across (a narrow
/// phone at a large text size), those that don't go on a line below, rather than off the screen.
struct ActionRowLayout: SwiftUI.Layout {
    var spacing: CGFloat = 12

    func sizeThatFits(proposal: ProposedViewSize, subviews: LayoutSubviews, cache: inout ()) -> CGSize {
        let lines = lines(of: subviews, width: proposal.width ?? .infinity)
        let height = lines.map(\.height).reduce(0, +) + spacing * CGFloat(max(lines.count - 1, 0))
        return CGSize(width: lines.map(\.width).max() ?? 0, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: LayoutSubviews, cache: inout ()) {
        var y = bounds.minY
        for line in lines(of: subviews, width: bounds.width) {
            var x = bounds.midX - line.width / 2
            for index in line.members {
                let size = subviews[index].sizeThatFits(.unspecified)
                // Each at its own ideal size: proposing the measured width back can round a hair short and wrap "Play".
                subviews[index].place(at: CGPoint(x: x, y: y + line.height / 2), anchor: .leading, proposal: .unspecified)
                x += size.width + spacing
            }
            y += line.height + spacing
        }
    }

    private struct Line {
        var members: [Int] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    private func lines(of subviews: LayoutSubviews, width: CGFloat) -> [Line] {
        var lines: [Line] = []
        var line = Line()
        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            guard size.width > 0 else { continue }
            if !line.members.isEmpty, line.width + spacing + size.width > width {
                lines.append(line)
                line = Line()
            }
            line.width += (line.members.isEmpty ? 0 : spacing) + size.width
            line.height = max(line.height, size.height)
            line.members.append(index)
        }
        if !line.members.isEmpty { lines.append(line) }
        return lines
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
                Text(t("You’re offline"))
                    .font(.body.weight(.medium))
                    .foregroundStyle(Color.screen)
            case .failed(let message, let retry):
                Image(systemName: "wifi.exclamationmark").font(.system(size: 40)).foregroundStyle(Color.usher)
                Text(message)
                    .font(.body.weight(.medium))
                    .foregroundStyle(Color.screen)
                    .multilineTextAlignment(.center)
                Button(t("Try again"), action: retry)
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
