import FlexarrKit
import SwiftUI

/// Primary/secondary action buttons (Play, More info, Mark watched). The main one rests in the title's ambient fill
/// (`Color.mainButton`) and the rest quietly on a translucent seat; focused, they light up in projected white with an
/// ambient glow, the way a first-party Apple TV button does.
struct ActionButtonStyle: ButtonStyle {
    /// The title's ambient colour, for the glow and the main button's fill; nil gives silver.
    var ambient: Color? = nil
    var prominent = false
    /// A round button showing only the label's icon; the title still names it for VoiceOver.
    var iconOnly = false

    func makeBody(configuration: Configuration) -> some View {
        ActionButtonBody(configuration: configuration, ambient: ambient, prominent: prominent, iconOnly: iconOnly)
    }

    private struct ActionButtonBody: View {
        let configuration: Configuration
        let ambient: Color?
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
                .foregroundStyle(isFocused || prominent ? Color.room : Color.screen)
                .background {
                    RoundedRectangle(cornerRadius: iconOnly ? 38 : Radius.control, style: .continuous)
                        .fill(isFocused ? AnyShapeStyle(Color.screen) : AnyShapeStyle(prominent ? Color.mainButton(ambient) : Color.screen.opacity(0.14)))
                }
                .shadow(color: isFocused ? (ambient ?? .silver).opacity(0.65) : .clear, radius: 28)
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
    /// What VoiceOver reads for the button, where its artwork has no words of its own (a mosaic of covers).
    var accessibilityText: String?
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
            .modifier(OptionalAccessibilityLabel(text: accessibilityText))
            .background {
                RoundedRectangle(cornerRadius: Radius.art)
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

private struct OptionalAccessibilityLabel: ViewModifier {
    let text: String?

    func body(content: Content) -> some View {
        if let text {
            content.accessibilityLabel(text)
        } else {
            content
        }
    }
}

struct SharedFocus: ViewModifier {
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

/// A round picture as a button (an artist, a person): it lifts and gains a ring and a glow when focused, as the square
/// cards light up; the system's borderless style barely changes a picture that isn't an Image.
struct CircleCardStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        CircleBody(configuration: configuration)
    }

    private struct CircleBody: View {
        let configuration: Configuration
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .clipShape(Circle())
                .overlay { Circle().strokeBorder(Color.screen, lineWidth: isFocused ? 5 : 0) }
                .shadow(color: isFocused ? Color.silver.opacity(0.45) : .clear, radius: 24)
                .scaleEffect(configuration.isPressed ? 1.04 : (isFocused ? 1.12 : 1))
                .animation(.spring(response: 0.3, dampingFraction: 0.75), value: isFocused)
                .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
        }
    }
}

/// Rows of cards on a page: moving up or down into a row lands on the card in the column focus left (or the row's
/// last), rather than wherever the focus engine picks, which can be far along the row. `column` is shared by the
/// page's rows; the cards take `focus` with their own ids.
struct RowFocus: ViewModifier {
    let ids: [String]
    @Binding var column: Int
    var focus: FocusState<String?>.Binding
    /// A grid's width, where `column` is the place across it; nil for a row.
    var columns: Int? = nil

    func body(content: Content) -> some View {
        content
            .defaultFocus(focus, ids.isEmpty ? nil : ids[min(column, ids.count - 1, (columns ?? Int.max) - 1)], priority: .userInitiated)
            .onChange(of: focus.wrappedValue) { _, id in
                guard let id, let index = ids.firstIndex(of: id) else { return }
                column = columns.map { index % $0 } ?? index
            }
    }
}

/// A block of text as a button: plain until focused, then lit in projected white like a track row. `filled` gives it
/// a quiet panel unfocused, for a card that should read as one (an author on a book's page).
struct TextCardStyle: ButtonStyle {
    var ambient: Color = .silver
    var filled = false

    func makeBody(configuration: Configuration) -> some View {
        CardBody(configuration: configuration, ambient: ambient, filled: filled)
    }

    private struct CardBody: View {
        let configuration: Configuration
        let ambient: Color
        let filled: Bool
        @Environment(\.isFocused) private var isFocused

        var body: some View {
            configuration.label
                .padding(.horizontal, 30)
                .padding(.vertical, 24)
                .foregroundStyle(isFocused ? Color.room : Color.screen)
                .background {
                    RoundedRectangle(cornerRadius: Radius.panel, style: .continuous)
                        .fill(isFocused ? AnyShapeStyle(Color.screen) : AnyShapeStyle(filled ? Color.screen.opacity(0.08) : Color.clear))
                }
                .shadow(color: isFocused ? ambient.opacity(0.5) : .clear, radius: 24)
                .scaleEffect(configuration.isPressed ? 0.98 : (isFocused ? 1.02 : 1))
                .animation(.spring(response: 0.3, dampingFraction: 0.75), value: isFocused)
                .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
        }
    }
}

/// Long text (a biography) clamped to a few lines; pressing it opens all of it. It reaches 30 points past its column
/// on each side, so the text lines up with what's above while the focus panel has room.
struct ReadMoreText: View {
    let title: String
    let text: String
    var lineLimit = 4
    var ambient: Color = .silver
    @State private var reading = false

    var body: some View {
        Button { reading = true } label: {
            Text(text)
                .font(.system(size: 27))
                .lineSpacing(5)
                .lineLimit(lineLimit)
                .multilineTextAlignment(.leading)
                .opacity(0.88)
                .frame(maxWidth: 1100, alignment: .leading)
        }
        .buttonStyle(TextCardStyle(ambient: ambient))
        .padding(.horizontal, -30)
        .accessibilityHint("Shows all of it")
        .fullScreenCover(isPresented: $reading) { FullTextView(title: title, text: text) }
    }
}

/// All of a long text, a paragraph at a time: each paragraph takes focus, so swiping scrolls through it. Menu closes it.
struct FullTextView: View {
    let title: String
    let text: String

    private var paragraphs: [[String]] {
        text.components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
            .map { Self.chunks(of: $0) }
    }

    /// The page scrolls by moving focus from one block to the next, so a block taller than the screen can't
    /// be read to its end. A long paragraph is split into groups of whole sentences (whole words for a very
    /// long sentence), each focusable, set close together so they still read as one paragraph.
    static func chunks(of paragraph: String, limit: Int = 600) -> [String] {
        guard paragraph.count > limit else { return [paragraph] }
        var pieces: [String] = []
        paragraph.enumerateSubstrings(in: paragraph.startIndex..., options: .bySentences) { _, _, range, _ in
            let sentence = String(paragraph[range])
            if sentence.count > limit {
                pieces.append(contentsOf: sentence.split(separator: " ").map { $0 + " " })
            } else {
                pieces.append(sentence)
            }
        }
        var chunks: [String] = []
        var current = ""
        for piece in pieces {
            if !current.isEmpty, current.count + piece.count > limit {
                chunks.append(current.trimmingCharacters(in: .whitespaces))
                current = ""
            }
            current += piece
        }
        let last = current.trimmingCharacters(in: .whitespaces)
        if !last.isEmpty { chunks.append(last) }
        return chunks.isEmpty ? [paragraph] : chunks
    }

    var body: some View {
        ZStack {
            Color.room.ignoresSafeArea()
            ScrollView(.vertical) {
                VStack(alignment: .leading, spacing: 30) {
                    Text(title)
                        .font(.marquee(56, weight: .heavy))
                        .foregroundStyle(Color.screen)
                        .padding(.bottom, 10)
                    ForEach(Array(paragraphs.enumerated()), id: \.offset) { _, chunks in
                        VStack(alignment: .leading, spacing: 8) {
                            ForEach(Array(chunks.enumerated()), id: \.offset) { _, chunk in
                                Text(chunk)
                                    .font(.system(size: 30))
                                    .lineSpacing(8)
                                    .foregroundStyle(Color.screen.opacity(0.9))
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .focusable()
                                    .focusEffectDisabled()
                            }
                        }
                    }
                }
                .frame(maxWidth: 1300, alignment: .leading)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 80)
            }
        }
    }
}
