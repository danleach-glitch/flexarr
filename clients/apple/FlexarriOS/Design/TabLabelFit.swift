import FlexarrKit
import UIKit

/// The tab bar's names drawn smaller when a longer language makes them run into each other (down to three quarters
/// of their size, as on Android), and at full size again when they fit. SwiftUI's tab bar takes no font of its own,
/// so this measures the bar UIKit draws and sets its items' title font.
@MainActor
enum TabLabelFit {
    /// The size the bar's names are drawn at before any shrinking, found the first time.
    private static var fullSize: CGFloat?

    static func apply() {
        for bar in tabBars() { fit(bar) }
    }

    private static func fit(_ bar: UITabBar) {
        guard let items = bar.items, !items.isEmpty else { return }
        // One per name: the selected tab can draw its name twice, one copy over the other.
        var labels: [UILabel] = []
        for label in bar.descendants(UILabel.self).filter({ !$0.isHidden && $0.alpha > 0 && !($0.text ?? "").isEmpty })
            .sorted(by: { $0.convert($0.bounds, to: bar).midX < $1.convert($1.bounds, to: bar).midX }) {
            if let last = labels.last, abs(last.convert(last.bounds, to: bar).midX - label.convert(label.bounds, to: bar).midX) < 4 { continue }
            labels.append(label)
        }
        guard labels.count >= 2, let font = labels.first?.font else { return }
        let full = fullSize ?? font.pointSize
        fullSize = full
        // Each name's natural width at full size, and the room it has: half the distance to each neighbour's centre.
        let base = font.withSize(full)
        let centres = labels.map { $0.convert($0.bounds, to: bar).midX }
        let widths = labels.map { ($0.text! as NSString).size(withAttributes: [.font: base]).width }
        var scale = 1.0
        for index in labels.indices.dropLast() {
            let share = Double(centres[index + 1] - centres[index])
            let pair = Double(widths[index] + widths[index + 1]) / 2
            scale = min(scale, LabelFit.scale(widths: [pair], share: share))
        }
        let size = (full * scale).rounded(.down)
        guard abs(font.pointSize - size) >= 0.5 else { return }
        let sized = base.withSize(size)
        for item in items {
            for state in [UIControl.State.normal, .selected] {
                var attributes = item.titleTextAttributes(for: state) ?? [:]
                attributes[.font] = sized
                item.setTitleTextAttributes(attributes, for: state)
            }
        }
    }

    private static func tabBars() -> [UITabBar] {
        UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .flatMap(\.windows)
            .flatMap { $0.descendants(UITabBar.self) }
            .filter { $0.window != nil && !$0.isHidden }
    }
}

private extension UIView {
    func descendants<T: UIView>(_ type: T.Type) -> [T] {
        subviews.flatMap { view -> [T] in
            if let match = view as? T { return [match] }
            return view.descendants(type)
        }
    }
}
