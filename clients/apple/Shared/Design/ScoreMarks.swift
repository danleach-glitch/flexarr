// Generated from design/scores.json by `make tokens`. Don't edit; redraw with scripts/design/draw_scores.py.

import SwiftUI

/// A score's source mark: a viewBox and filled paths (docs/design.md › Scores).
struct ScoreMarkDrawing {
    let width: CGFloat
    let height: CGFloat
    let paths: [(fill: Color, path: Path)]
}

enum ScoreMarks {
    /// IMDb, beside the score out of 10.
    static let imdb = ScoreMarkDrawing(width: 64, height: 32, paths: [
        (Color(.sRGB, red: 245 / 255, green: 197 / 255, blue: 24 / 255), Path { p in
            p.move(to: CGPoint(x: 5, y: 0))
            p.addLine(to: CGPoint(x: 59, y: 0))
            p.addCurve(to: CGPoint(x: 64, y: 5), control1: CGPoint(x: 61.76, y: 0), control2: CGPoint(x: 64, y: 2.24))
            p.addLine(to: CGPoint(x: 64, y: 27))
            p.addCurve(to: CGPoint(x: 59, y: 32), control1: CGPoint(x: 64, y: 29.76), control2: CGPoint(x: 61.76, y: 32))
            p.addLine(to: CGPoint(x: 5, y: 32))
            p.addCurve(to: CGPoint(x: 0, y: 27), control1: CGPoint(x: 2.24, y: 32), control2: CGPoint(x: 0, y: 29.76))
            p.addLine(to: CGPoint(x: 0, y: 5))
            p.addCurve(to: CGPoint(x: 5, y: 0), control1: CGPoint(x: 0, y: 2.24), control2: CGPoint(x: 2.24, y: 0))
            p.closeSubpath()
        }),
        (Color(.sRGB, red: 0 / 255, green: 0 / 255, blue: 0 / 255), Path { p in
            p.move(to: CGPoint(x: 8, y: 7))
            p.addLine(to: CGPoint(x: 12, y: 7))
            p.addLine(to: CGPoint(x: 12, y: 25))
            p.addLine(to: CGPoint(x: 8, y: 25))
            p.closeSubpath()
        }),
        (Color(.sRGB, red: 0 / 255, green: 0 / 255, blue: 0 / 255), Path { p in
            p.move(to: CGPoint(x: 14, y: 7))
            p.addLine(to: CGPoint(x: 19, y: 7))
            p.addLine(to: CGPoint(x: 22, y: 17))
            p.addLine(to: CGPoint(x: 25, y: 7))
            p.addLine(to: CGPoint(x: 30, y: 7))
            p.addLine(to: CGPoint(x: 30, y: 25))
            p.addLine(to: CGPoint(x: 26.5, y: 25))
            p.addLine(to: CGPoint(x: 26.5, y: 13.5))
            p.addLine(to: CGPoint(x: 23.5, y: 25))
            p.addLine(to: CGPoint(x: 20.5, y: 25))
            p.addLine(to: CGPoint(x: 17.5, y: 13.5))
            p.addLine(to: CGPoint(x: 17.5, y: 25))
            p.addLine(to: CGPoint(x: 14, y: 25))
            p.closeSubpath()
        }),
        (Color(.sRGB, red: 0 / 255, green: 0 / 255, blue: 0 / 255), Path { p in
            p.move(to: CGPoint(x: 32, y: 7))
            p.addLine(to: CGPoint(x: 38, y: 7))
            p.addCurve(to: CGPoint(x: 44, y: 16), control1: CGPoint(x: 42.5, y: 7), control2: CGPoint(x: 44, y: 9.5))
            p.addCurve(to: CGPoint(x: 38, y: 25), control1: CGPoint(x: 44, y: 22.5), control2: CGPoint(x: 42.5, y: 25))
            p.addLine(to: CGPoint(x: 32, y: 25))
            p.closeSubpath()
        }),
        (Color(.sRGB, red: 245 / 255, green: 197 / 255, blue: 24 / 255), Path { p in
            p.move(to: CGPoint(x: 35.5, y: 10.5))
            p.addLine(to: CGPoint(x: 37.5, y: 10.5))
            p.addCurve(to: CGPoint(x: 40.5, y: 16), control1: CGPoint(x: 39.8, y: 10.5), control2: CGPoint(x: 40.5, y: 12))
            p.addCurve(to: CGPoint(x: 37.5, y: 21.5), control1: CGPoint(x: 40.5, y: 20), control2: CGPoint(x: 39.8, y: 21.5))
            p.addLine(to: CGPoint(x: 35.5, y: 21.5))
            p.closeSubpath()
        }),
        (Color(.sRGB, red: 0 / 255, green: 0 / 255, blue: 0 / 255), Path { p in
            p.move(to: CGPoint(x: 46, y: 7))
            p.addLine(to: CGPoint(x: 49.5, y: 7))
            p.addLine(to: CGPoint(x: 49.5, y: 25))
            p.addLine(to: CGPoint(x: 46, y: 25))
            p.closeSubpath()
        }),
        (Color(.sRGB, red: 0 / 255, green: 0 / 255, blue: 0 / 255), Path { p in
            p.move(to: CGPoint(x: 49.5, y: 13))
            p.addCurve(to: CGPoint(x: 53, y: 12), control1: CGPoint(x: 51, y: 12.2), control2: CGPoint(x: 52, y: 12))
            p.addCurve(to: CGPoint(x: 56.5, y: 18.5), control1: CGPoint(x: 55.6, y: 12), control2: CGPoint(x: 56.5, y: 14))
            p.addCurve(to: CGPoint(x: 53, y: 25), control1: CGPoint(x: 56.5, y: 23), control2: CGPoint(x: 55.6, y: 25))
            p.addCurve(to: CGPoint(x: 49.5, y: 23.6), control1: CGPoint(x: 52, y: 25), control2: CGPoint(x: 51, y: 24.6))
            p.closeSubpath()
        }),
        (Color(.sRGB, red: 245 / 255, green: 197 / 255, blue: 24 / 255), Path { p in
            p.move(to: CGPoint(x: 49.5, y: 15.6))
            p.addCurve(to: CGPoint(x: 51.7, y: 15), control1: CGPoint(x: 50.4, y: 15.2), control2: CGPoint(x: 51, y: 15))
            p.addCurve(to: CGPoint(x: 53.2, y: 18.5), control1: CGPoint(x: 52.8, y: 15), control2: CGPoint(x: 53.2, y: 16))
            p.addCurve(to: CGPoint(x: 51.7, y: 22), control1: CGPoint(x: 53.2, y: 21), control2: CGPoint(x: 52.8, y: 22))
            p.addCurve(to: CGPoint(x: 49.5, y: 21.3), control1: CGPoint(x: 51, y: 22), control2: CGPoint(x: 50.4, y: 21.8))
            p.closeSubpath()
        }),
    ])
    /// Rotten Tomatoes Certified Fresh.
    static let certifiedFresh = ScoreMarkDrawing(width: 32, height: 32, paths: [
        (Color(.sRGB, red: 250 / 255, green: 50 / 255, blue: 10 / 255), Path { p in
            p.move(to: CGPoint(x: 16, y: 2.6))
            p.addLine(to: CGPoint(x: 18.42, y: 3.84))
            p.addLine(to: CGPoint(x: 21.13, y: 3.62))
            p.addLine(to: CGPoint(x: 22.89, y: 5.69))
            p.addLine(to: CGPoint(x: 25.48, y: 6.52))
            p.addLine(to: CGPoint(x: 26.31, y: 9.11))
            p.addLine(to: CGPoint(x: 28.38, y: 10.87))
            p.addLine(to: CGPoint(x: 28.16, y: 13.58))
            p.addLine(to: CGPoint(x: 29.4, y: 16))
            p.addLine(to: CGPoint(x: 28.16, y: 18.42))
            p.addLine(to: CGPoint(x: 28.38, y: 21.13))
            p.addLine(to: CGPoint(x: 26.31, y: 22.89))
            p.addLine(to: CGPoint(x: 25.48, y: 25.48))
            p.addLine(to: CGPoint(x: 22.89, y: 26.31))
            p.addLine(to: CGPoint(x: 21.13, y: 28.38))
            p.addLine(to: CGPoint(x: 18.42, y: 28.16))
            p.addLine(to: CGPoint(x: 16, y: 29.4))
            p.addLine(to: CGPoint(x: 13.58, y: 28.16))
            p.addLine(to: CGPoint(x: 10.87, y: 28.38))
            p.addLine(to: CGPoint(x: 9.11, y: 26.31))
            p.addLine(to: CGPoint(x: 6.52, y: 25.48))
            p.addLine(to: CGPoint(x: 5.69, y: 22.89))
            p.addLine(to: CGPoint(x: 3.62, y: 21.13))
            p.addLine(to: CGPoint(x: 3.84, y: 18.42))
            p.addLine(to: CGPoint(x: 2.6, y: 16))
            p.addLine(to: CGPoint(x: 3.84, y: 13.58))
            p.addLine(to: CGPoint(x: 3.62, y: 10.87))
            p.addLine(to: CGPoint(x: 5.69, y: 9.11))
            p.addLine(to: CGPoint(x: 6.52, y: 6.52))
            p.addLine(to: CGPoint(x: 9.11, y: 5.69))
            p.addLine(to: CGPoint(x: 10.87, y: 3.62))
            p.addLine(to: CGPoint(x: 13.58, y: 3.84))
            p.closeSubpath()
        }),
        (Color(.sRGB, red: 211 / 255, green: 207 / 255, blue: 201 / 255), Path { p in
            p.move(to: CGPoint(x: 16, y: 4.5))
            p.addCurve(to: CGPoint(x: 27.5, y: 16), control1: CGPoint(x: 22.35, y: 4.5), control2: CGPoint(x: 27.5, y: 9.65))
            p.addCurve(to: CGPoint(x: 16, y: 27.5), control1: CGPoint(x: 27.5, y: 22.35), control2: CGPoint(x: 22.35, y: 27.5))
            p.addCurve(to: CGPoint(x: 4.5, y: 16), control1: CGPoint(x: 9.65, y: 27.5), control2: CGPoint(x: 4.5, y: 22.35))
            p.addCurve(to: CGPoint(x: 16, y: 4.5), control1: CGPoint(x: 4.5, y: 9.65), control2: CGPoint(x: 9.65, y: 4.5))
            p.closeSubpath()
        }),
        (Color(.sRGB, red: 250 / 255, green: 50 / 255, blue: 10 / 255), Path { p in
            p.move(to: CGPoint(x: 16, y: 8.28))
            p.addCurve(to: CGPoint(x: 25.36, y: 16.86), control1: CGPoint(x: 21.17, y: 8.28), control2: CGPoint(x: 25.36, y: 12.12))
            p.addCurve(to: CGPoint(x: 16, y: 25.44), control1: CGPoint(x: 25.36, y: 21.6), control2: CGPoint(x: 21.17, y: 25.44))
            p.addCurve(to: CGPoint(x: 6.64, y: 16.86), control1: CGPoint(x: 10.83, y: 25.44), control2: CGPoint(x: 6.64, y: 21.6))
            p.addCurve(to: CGPoint(x: 16, y: 8.28), control1: CGPoint(x: 6.64, y: 12.12), control2: CGPoint(x: 10.83, y: 8.28))
            p.closeSubpath()
        }),
        (Color(.sRGB, red: 0 / 255, green: 145 / 255, blue: 45 / 255), Path { p in
            p.move(to: CGPoint(x: 16, y: 9.37))
            p.addLine(to: CGPoint(x: 12.41, y: 6.56))
            p.addLine(to: CGPoint(x: 13.97, y: 9.21))
            p.addLine(to: CGPoint(x: 11.01, y: 9.53))
            p.addLine(to: CGPoint(x: 14.28, y: 10.46))
            p.addLine(to: CGPoint(x: 16, y: 11.87))
            p.addLine(to: CGPoint(x: 17.72, y: 10.46))
            p.addLine(to: CGPoint(x: 20.99, y: 9.53))
            p.addLine(to: CGPoint(x: 18.03, y: 9.21))
            p.addLine(to: CGPoint(x: 19.59, y: 6.56))
            p.closeSubpath()
        }),
    ])
    /// Rotten Tomatoes Fresh, 60% and up.
    static let fresh = ScoreMarkDrawing(width: 32, height: 32, paths: [
        (Color(.sRGB, red: 250 / 255, green: 50 / 255, blue: 10 / 255), Path { p in
            p.move(to: CGPoint(x: 16, y: 6.1))
            p.addCurve(to: CGPoint(x: 28, y: 17.1), control1: CGPoint(x: 22.63, y: 6.1), control2: CGPoint(x: 28, y: 11.02))
            p.addCurve(to: CGPoint(x: 16, y: 28.1), control1: CGPoint(x: 28, y: 23.18), control2: CGPoint(x: 22.63, y: 28.1))
            p.addCurve(to: CGPoint(x: 4, y: 17.1), control1: CGPoint(x: 9.37, y: 28.1), control2: CGPoint(x: 4, y: 23.18))
            p.addCurve(to: CGPoint(x: 16, y: 6.1), control1: CGPoint(x: 4, y: 11.02), control2: CGPoint(x: 9.37, y: 6.1))
            p.closeSubpath()
        }),
        (Color(.sRGB, red: 0 / 255, green: 145 / 255, blue: 45 / 255), Path { p in
            p.move(to: CGPoint(x: 16, y: 7.5))
            p.addLine(to: CGPoint(x: 11.4, y: 3.9))
            p.addLine(to: CGPoint(x: 13.4, y: 7.3))
            p.addLine(to: CGPoint(x: 9.6, y: 7.7))
            p.addLine(to: CGPoint(x: 13.8, y: 8.9))
            p.addLine(to: CGPoint(x: 16, y: 10.7))
            p.addLine(to: CGPoint(x: 18.2, y: 8.9))
            p.addLine(to: CGPoint(x: 22.4, y: 7.7))
            p.addLine(to: CGPoint(x: 18.6, y: 7.3))
            p.addLine(to: CGPoint(x: 20.6, y: 3.9))
            p.closeSubpath()
        }),
    ])
    /// Rotten Tomatoes Rotten, under 60%.
    static let rotten = ScoreMarkDrawing(width: 32, height: 32, paths: [
        (Color(.sRGB, red: 10 / 255, green: 200 / 255, blue: 85 / 255), Path { p in
            p.move(to: CGPoint(x: 28.9, y: 16.5))
            p.addLine(to: CGPoint(x: 29.69, y: 17.92))
            p.addLine(to: CGPoint(x: 28.72, y: 19.17))
            p.addLine(to: CGPoint(x: 29, y: 20.69))
            p.addLine(to: CGPoint(x: 23.74, y: 19.93))
            p.addLine(to: CGPoint(x: 22, y: 19.97))
            p.addLine(to: CGPoint(x: 21.58, y: 20.6))
            p.addLine(to: CGPoint(x: 22.05, y: 22.06))
            p.addLine(to: CGPoint(x: 22.47, y: 23.96))
            p.addLine(to: CGPoint(x: 22.11, y: 25.45))
            p.addLine(to: CGPoint(x: 20.88, y: 25.82))
            p.addLine(to: CGPoint(x: 20.18, y: 27.49))
            p.addLine(to: CGPoint(x: 17.85, y: 23.94))
            p.addLine(to: CGPoint(x: 16.85, y: 23.48))
            p.addLine(to: CGPoint(x: 16, y: 24.3))
            p.addLine(to: CGPoint(x: 14.82, y: 26.12))
            p.addLine(to: CGPoint(x: 13.16, y: 27.95))
            p.addLine(to: CGPoint(x: 11.37, y: 28.68))
            p.addLine(to: CGPoint(x: 10.08, y: 27.81))
            p.addLine(to: CGPoint(x: 8.2, y: 27.92))
            p.addLine(to: CGPoint(x: 9.98, y: 23.45))
            p.addLine(to: CGPoint(x: 10.19, y: 21.85))
            p.addLine(to: CGPoint(x: 9.58, y: 21.21))
            p.addLine(to: CGPoint(x: 8.01, y: 21.12))
            p.addLine(to: CGPoint(x: 6.1, y: 20.88))
            p.addLine(to: CGPoint(x: 4.91, y: 20.07))
            p.addLine(to: CGPoint(x: 5.16, y: 18.78))
            p.addLine(to: CGPoint(x: 3.9, y: 17.75))
            p.addLine(to: CGPoint(x: 8.5, y: 16.5))
            p.addLine(to: CGPoint(x: 9.42, y: 15.82))
            p.addLine(to: CGPoint(x: 8.83, y: 14.99))
            p.addLine(to: CGPoint(x: 7.11, y: 13.64))
            p.addLine(to: CGPoint(x: 5.36, y: 11.79))
            p.addLine(to: CGPoint(x: 4.73, y: 9.98))
            p.addLine(to: CGPoint(x: 5.63, y: 8.89))
            p.addLine(to: CGPoint(x: 5.64, y: 6.97))
            p.addLine(to: CGPoint(x: 9.75, y: 9.29))
            p.addLine(to: CGPoint(x: 11.26, y: 9.56))
            p.addLine(to: CGPoint(x: 12.03, y: 8.91))
            p.addLine(to: CGPoint(x: 12.52, y: 7.35))
            p.addLine(to: CGPoint(x: 13.31, y: 5.64))
            p.addLine(to: CGPoint(x: 14.57, y: 4.83))
            p.addLine(to: CGPoint(x: 16, y: 5.53))
            p.addLine(to: CGPoint(x: 17.42, y: 4.91))
            p.addLine(to: CGPoint(x: 17.7, y: 9.65))
            p.addLine(to: CGPoint(x: 18.11, y: 10.96))
            p.addLine(to: CGPoint(x: 18.93, y: 10.9))
            p.addLine(to: CGPoint(x: 20.55, y: 9.84))
            p.addLine(to: CGPoint(x: 22.7, y: 8.77))
            p.addLine(to: CGPoint(x: 24.61, y: 8.57))
            p.addLine(to: CGPoint(x: 25.53, y: 9.51))
            p.addLine(to: CGPoint(x: 27.65, y: 9.77))
            p.addLine(to: CGPoint(x: 24.48, y: 12.74))
            p.addLine(to: CGPoint(x: 24.17, y: 13.87))
            p.addLine(to: CGPoint(x: 25.05, y: 14.6))
            p.addLine(to: CGPoint(x: 26.95, y: 15.36))
            p.closeSubpath()
        }),
    ])
}
