import SwiftUI

/// Light in the cover's colours, drifting behind Now Playing. Large soft fields wander on slow, never-repeating paths;
/// the bass makes them swell, a beat makes them flare, the middle hurries them along and the treble brings out small
/// bright glints. With nothing to hear (paused, or a converted stream that can't be tapped) they drift on their own.
/// The web app's visualiser draws the same way (frontend/src/components/Aurora.tsx).
///
/// Drawn about a tenth of the screen's size, blurred and scaled up: a handful of gradients a frame, 30 times a second.
struct MusicAurora: View {
    var colours: [CoverPalette.RGB]
    var spectrum: AudioSpectrum
    var playing: Bool

    @State private var motion = AuroraMotion()
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private static let canvas = CGSize(width: 192, height: 108)

    var body: some View {
        GeometryReader { geometry in
            TimelineView(.animation(minimumInterval: 1 / 30, paused: reduceMotion)) { timeline in
                let frame = motion.step(at: timeline.date, bands: playing ? spectrum.bands : nil, playing: playing && !reduceMotion)
                Canvas { context, size in draw(frame, in: &context, size: size) }
                    .frame(width: Self.canvas.width, height: Self.canvas.height)
                    .blur(radius: 5)
                    .drawingGroup()
                    // Overscan, so the blur's soft edge stays off screen.
                    .scaleEffect(x: geometry.size.width / Self.canvas.width * 1.16, y: geometry.size.height / Self.canvas.height * 1.16)
                    .frame(width: geometry.size.width, height: geometry.size.height)
            }
        }
        .overlay {
            // Keeps the words and controls legible on the brightest light.
            ZStack {
                RadialGradient(colors: [.clear, .black.opacity(0.55)], center: .center, startRadius: 300, endRadius: 1300)
                LinearGradient(colors: [.black.opacity(0.15), .clear, .clear, .black.opacity(0.35)], startPoint: .top, endPoint: .bottom)
                Color.black.opacity(0.24)
            }
        }
        .ignoresSafeArea()
        .accessibilityHidden(true)
    }

    private func draw(_ frame: AuroraMotion.Frame, in context: inout GraphicsContext, size: CGSize) {
        let base = colours.first ?? CoverPalette.RGB(r: 120, g: 100, b: 170)
        context.fill(Path(CGRect(origin: .zero, size: size)),
                     with: .color(Color(red: (base.r * 0.08 + 6) / 255, green: (base.g * 0.08 + 5) / 255, blue: (base.b * 0.08 + 10) / 255)))
        context.blendMode = .screen
        let w = size.width, h = size.height, span = max(w, h)
        let clock = frame.clock

        // Five large fields, each on its own Lissajous path, the cover's colours shared out between them.
        for i in 0..<5 {
            let c = colours.isEmpty ? base : colours[i % colours.count]
            let n = Double(i)
            let t = clock * (0.55 + n * 0.13) + n * 1.7
            let x = w * (0.5 + 0.42 * sin(t * 0.71 + n) * cos(t * 0.23))
            let y = h * (0.5 + 0.38 * cos(t * 0.53 + n * 2.1) * sin(t * 0.31 + 0.6))
            // Low fields (the first two) answer the bass; the rest the middle.
            let drive = i < 2 ? frame.bass : frame.mid
            let radius = span * (0.36 + 0.06 * sin(t * 0.9)) * (1 + 0.22 * drive + 0.18 * frame.pulse)
            let alpha = 0.28 + 0.2 * drive + 0.16 * frame.pulse
            fill(&context, size: size, at: CGPoint(x: x, y: y), radius: radius,
                 stops: [(c, alpha, 0), (c, alpha * 0.35, 0.55), (c, 0, 1)])
        }

        // Glints: small, quicker, nearly white versions of the colours, there only while the treble is.
        guard frame.high > 0.05 else { return }
        for i in 0..<4 {
            let c = colours.isEmpty ? base : colours[(i + 1) % colours.count]
            let n = Double(i)
            let t = clock * (1.6 + n * 0.37) + n * 2.3
            let x = w * (0.5 + 0.45 * sin(t * 0.83 + n * 1.3))
            let y = h * (0.5 + 0.4 * sin(t * 0.61 + n * 0.7) * cos(t * 0.29))
            let alpha = 0.5 * frame.high * (0.6 + 0.4 * sin(t * 3.1 + n))
            let bright = CoverPalette.RGB(r: (c.r + 510) / 3, g: (c.g + 510) / 3, b: (c.b + 510) / 3)
            fill(&context, size: size, at: CGPoint(x: x, y: y), radius: span * (0.09 + 0.05 * frame.high),
                 stops: [(bright, alpha, 0), (c, 0, 1)])
        }
    }

    private func fill(_ context: inout GraphicsContext, size: CGSize, at centre: CGPoint, radius: CGFloat,
                      stops: [(CoverPalette.RGB, Double, Double)]) {
        let gradient = Gradient(stops: stops.map { c, alpha, location in
            .init(color: Color(red: c.r / 255, green: c.g / 255, blue: c.b / 255).opacity(alpha), location: location)
        })
        context.fill(Path(CGRect(origin: .zero, size: size)),
                     with: .radialGradient(gradient, center: centre, startRadius: 0, endRadius: radius))
    }
}

/// The light's state from frame to frame: its clock, how fast it's going, and the music's levels, smoothed. Each band
/// keeps a slowly sinking peak to measure against, so a quiet recording moves as much as a loud one; levels rise
/// quickly and fall slowly, so the light breathes rather than flickers.
@MainActor
final class AuroraMotion {
    struct Frame {
        var clock: Double
        var bass: Double
        var mid: Double
        var high: Double
        var pulse: Double
    }

    private var last: Date?
    private var clock = 0.0
    private var pace = 0.02
    private var peak = (bass: 0.2, mid: 0.2, high: 0.2)
    private var level = (bass: 0.0, mid: 0.0, high: 0.0)
    private var slowBass = 0.0
    private var pulse = 0.0

    func step(at date: Date, bands: AudioSpectrum.Bands?, playing: Bool) -> Frame {
        // A long gap (the screen was away) counts as one ordinary frame, so nothing leaps when it comes back.
        let dt = min(0.1, max(0, date.timeIntervalSince(last ?? date)))
        last = date
        func ease(_ from: Double, _ to: Double, _ rate: Double) -> Double { from + (to - from) * (1 - exp(-rate * dt)) }
        func follow(_ raw: Double, _ peak: inout Double, _ level: inout Double) {
            peak = max(raw, 0.08, peak * exp(-dt * 0.09))
            let target = min(1, raw / peak)
            level = ease(level, target, target > level ? 14 : 2.5)
        }
        follow(Double(bands?.bass ?? 0), &peak.bass, &level.bass)
        follow(Double(bands?.mid ?? 0), &peak.mid, &level.mid)
        follow(Double(bands?.high ?? 0), &peak.high, &level.high)
        slowBass = ease(slowBass, level.bass, 1.2)
        let kick = min(1, max(0, level.bass - slowBass) * 2.4)
        pulse = ease(pulse, kick, kick > pulse ? 20 : 3)
        // Playing, it drifts and the middle quickens it; paused, it slows almost to a stop.
        let target = playing ? 0.05 + 0.12 * level.mid + 0.05 * pulse : 0.008
        pace = ease(pace, target, 1.5)
        clock += pace * dt * 4
        return Frame(clock: clock, bass: level.bass, mid: level.mid, high: level.high, pulse: pulse)
    }
}
