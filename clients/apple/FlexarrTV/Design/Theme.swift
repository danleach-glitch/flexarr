import FlexarrKit
import SwiftUI

// TV sizes. The colour tokens, marquee type and room background are shared (Shared/Design/Colors.swift).

enum Layout {
    /// The shared left edge for heroes, rows and grids (tvOS title-safe inset).
    static let edge: CGFloat = 80
    static let posterWidth: CGFloat = 250
    static let wideWidth: CGFloat = 500
    static let rowSpacing: CGFloat = 44
    /// Six posters exactly fill the 1920 - 2 * 80 content width.
    static let gridSpacing: CGFloat = (1920 - 2 * edge - 6 * posterWidth) / 5
}

/// "2016   1h 56m   TV-MA": facts separated by space, never middle dots.
struct FactsLine: View {
    let facts: [String]

    var body: some View {
        HStack(spacing: 22) {
            ForEach(Array(facts.enumerated()), id: \.offset) { _, fact in
                Text(fact)
            }
        }
        .font(.system(size: 26, weight: .semibold))
        .foregroundStyle(Color.screen.opacity(0.85))
        .monospacedDigit()
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
                Capsule().fill(tint).frame(width: max(8, proxy.size.width * value))
            }
        }
        .frame(height: 7)
    }
}

/// Section heading for rows: marquee type, left-aligned on the shared edge.
struct RowHeading: View {
    let title: String

    var body: some View {
        Text(title)
            .font(.marquee(40, weight: .bold))
            .foregroundStyle(Color.screen)
            .padding(.leading, Layout.edge)
    }
}
