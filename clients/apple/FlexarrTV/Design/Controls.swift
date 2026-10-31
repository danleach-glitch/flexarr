import FlexarrKit
import SwiftUI

/// Primary/secondary action buttons (Play, More info, Mark watched). Unfocused they sit
/// quietly on a translucent seat; focused they light up in projected white with an
/// ambient glow, the way a first-party Apple TV button does.
struct ActionButtonStyle: ButtonStyle {
    var ambient: Color = .silver
    var prominent = false
    /// A round button showing only the label's icon; the title still names it for VoiceOver.
    var iconOnly = false

    func makeBody(configuration: Configuration) -> some View {
        ActionButtonBody(configuration: configuration, ambient: ambient, prominent: prominent, iconOnly: iconOnly)
    }

    private struct ActionButtonBody: View {
        let configuration: Configuration
        let ambient: Color
        let prominent: Bool
        let iconOnly: Bool
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .font(.system(size: 29, weight: .semibold))
                .labelStyle(ActionLabelStyle(iconOnly: iconOnly))
                .lineLimit(1)
                .fixedSize()
                .padding(.horizontal, iconOnly ? 0 : 36)
                .frame(width: iconOnly ? 76 : nil, height: 76)
                .foregroundStyle(isFocused ? Color.room : Color.screen)
                .background {
                    RoundedRectangle(cornerRadius: iconOnly ? 38 : 14, style: .continuous)
                        .fill(isFocused ? AnyShapeStyle(Color.screen) : AnyShapeStyle(prominent ? ambient.opacity(0.42) : Color.screen.opacity(0.14)))
                }
                .shadow(color: isFocused ? ambient.opacity(0.65) : .clear, radius: 28)
                .scaleEffect(configuration.isPressed ? 0.97 : (isFocused ? 1.07 : 1))
                .animation(.spring(response: 0.3, dampingFraction: 0.7), value: isFocused)
                .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
        }
    }

    private struct ActionLabelStyle: LabelStyle {
        let iconOnly: Bool

        func makeBody(configuration: Configuration) -> some View {
            if iconOnly {
                configuration.icon.font(.system(size: 28, weight: .bold))
            } else {
                HStack(spacing: 14) {
                    configuration.icon.font(.system(size: 26, weight: .bold))
                    configuration.title
                }
            }
        }
    }
}

/// Small selectable pill for season and sort pickers. `selected` stays highlighted while
/// focus moves elsewhere so the choice is visible.
struct PillButtonStyle: ButtonStyle {
    var selected: Bool
    var ambient: Color = .silver

    func makeBody(configuration: Configuration) -> some View {
        PillBody(configuration: configuration, selected: selected, ambient: ambient)
    }

    private struct PillBody: View {
        let configuration: Configuration
        let selected: Bool
        let ambient: Color
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .font(.system(size: 26, weight: selected || isFocused ? .bold : .medium))
                .padding(.horizontal, 30)
                .frame(height: 62)
                .foregroundStyle(isFocused ? Color.room : (selected ? Color.screen : Color.usher))
                .background {
                    Capsule().fill(isFocused ? Color.screen : (selected ? ambient.opacity(0.32) : Color.clear))
                }
                .overlay {
                    if !isFocused && !selected { Capsule().strokeBorder(Color.aisle, lineWidth: 2) }
                }
                .shadow(color: isFocused ? ambient.opacity(0.55) : .clear, radius: 22)
                .scaleEffect(isFocused ? 1.06 : 1)
                .animation(.spring(response: 0.3, dampingFraction: 0.75), value: isFocused)
        }
    }
}

/// Artwork button: the native tvOS card lift (parallax, specular highlight) on the
/// artwork, with an ambient glow and the caption below brightening on focus.
struct ArtworkButton<Artwork: View, Caption: View>: View {
    let ambient: Color
    /// Optional shared focus state so a row can steer focus to a particular card.
    var focus: FocusState<String?>.Binding?
    var focusID: String?
    let action: () -> Void
    @ViewBuilder var artwork: () -> Artwork
    @ViewBuilder var caption: (_ focused: Bool) -> Caption

    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Button(action: action) {
                artwork()
            }
            .buttonStyle(.card)
            .focused($focused)
            .modifier(SharedFocus(binding: focus, id: focusID))
            .background {
                RoundedRectangle(cornerRadius: 12)
                    .fill(ambient)
                    .blur(radius: 34)
                    .opacity(focused ? 0.55 : 0)
                    .scaleEffect(1.06)
            }
            .animation(.easeOut(duration: 0.25), value: focused)

            caption(focused)
                .offset(y: focused ? 14 : 0)
                .animation(.spring(response: 0.3, dampingFraction: 0.8), value: focused)
        }
    }
}

private struct SharedFocus: ViewModifier {
    let binding: FocusState<String?>.Binding?
    let id: String?

    func body(content: Content) -> some View {
        if let binding, let id {
            content.focused(binding, equals: id)
        } else {
            content
        }
    }
}
