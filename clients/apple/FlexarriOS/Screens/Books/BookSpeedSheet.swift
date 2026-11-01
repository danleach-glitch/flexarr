import FlexarrKit
import SwiftUI

/// A book's speed, 0.5× to 3.5× in 0.05 steps: a slider, finer steps either side, and the usual speeds a tap away.
/// Kept for this book; the book page's default comes from Settings.
struct BookSpeedSheet: View {
    @Environment(AppModel.self) private var model
    private var music: MusicPlayer { model.music }
    private static let presets: [Double] = [1, 1.25, 1.5, 1.75, 2]

    var body: some View {
        VStack(spacing: 22) {
            Text(BookSpeed.label(music.speed))
                .font(.marquee(40, weight: .black))
                .foregroundStyle(Color.screen)
                .monospacedDigit()
                .contentTransition(.numericText())
                .padding(.top, 24)
            HStack(spacing: 16) {
                step(-BookSpeed.step, "minus")
                Slider(value: Binding(get: { music.speed }, set: { music.setSpeed($0) }), in: BookSpeed.range, step: BookSpeed.step)
                    .tint(Color.silver)
                    .accessibilityValue(BookSpeed.label(music.speed))
                step(BookSpeed.step, "plus")
            }
            HStack(spacing: 8) {
                ForEach(Self.presets, id: \.self) { speed in
                    Button(BookSpeed.label(speed)) { music.setSpeed(speed) }
                        .buttonStyle(PillButtonStyle(selected: abs(music.speed - speed) < 0.001))
                        .monospacedDigit()
                }
            }
        }
        .padding(.horizontal, 24)
        .frame(maxHeight: .infinity, alignment: .top)
        .sensoryFeedback(.selection, trigger: music.speed)
    }

    private func step(_ by: Double, _ symbol: String) -> some View {
        Button { music.setSpeed(music.speed + by) } label: {
            Image(systemName: symbol)
                .font(.body.weight(.bold))
                .foregroundStyle(Color.screen)
                .frame(width: 44, height: 44)
                .background(Circle().fill(Color.screen.opacity(0.14)))
        }
        .buttonStyle(.plain)
        .accessibilityLabel(by > 0 ? "Faster" : "Slower")
    }
}
