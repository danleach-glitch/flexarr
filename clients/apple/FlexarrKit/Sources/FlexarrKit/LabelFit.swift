import Foundation

/// How much to shrink a row of labels that share a width equally (a tab bar's names, say) so the widest fits its
/// share with a gap either side: 1 when they all fit already, never below `minimum` (past that they're cut short
/// instead, which beats unreadable).
public enum LabelFit {
    public static func scale(widths: [Double], share: Double, gap: Double = 6, minimum: Double = 0.75) -> Double {
        guard let widest = widths.max(), widest > 0, share > gap else { return 1 }
        return min(1, max(minimum, (share - gap) / widest))
    }
}
