import FlexarrKit
import SwiftUI

/// A score's source mark (design/scores.json), drawn at `height`.
struct ScoreMarkView: View {
    let drawing: ScoreMarkDrawing
    let height: CGFloat

    var body: some View {
        Canvas { context, size in
            let scale = CGAffineTransform(scaleX: size.width / drawing.width, y: size.height / drawing.height)
            for (fill, path) in drawing.paths {
                context.fill(path.applying(scale), with: .color(fill))
            }
        }
        .frame(width: height * drawing.width / drawing.height, height: height)
        .accessibilityHidden(true)
    }
}

/// IMDb and Rotten Tomatoes, each mark beside its number (docs/design.md › Scores). Takes the surrounding font.
struct ScoreChips: View {
    let scores: Scores
    let markHeight: CGFloat
    let spacing: CGFloat

    var body: some View {
        HStack(spacing: spacing) {
            if let text = scores.imdbText {
                chip(ScoreMarks.imdb, text, label: scores.imdbLabel)
            }
            if let rt = scores.rottenTomatoes {
                chip(Self.tomato(rt.state), "\(rt.score)%", label: scores.rottenTomatoesLabel)
            }
        }
    }

    private func chip(_ drawing: ScoreMarkDrawing, _ text: String, label: String?) -> some View {
        HStack(spacing: markHeight * 0.4) {
            ScoreMarkView(drawing: drawing, height: markHeight)
            Text(text)
        }
        .fixedSize()
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(label ?? text)
    }

    static func tomato(_ state: RottenTomatoesState) -> ScoreMarkDrawing {
        switch state {
        case .certifiedFresh: ScoreMarks.certifiedFresh
        case .rotten: ScoreMarks.rotten
        case .fresh, .unknown: ScoreMarks.fresh
        }
    }
}
