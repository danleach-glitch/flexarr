import SwiftUI

// Here rather than beside TabStack: the Mac app compiles these pages without the phone's tab bar.
extension EnvironmentValues {
    /// How much of the bottom of a tab's pages the mini player and the other bars over them cover.
    @Entry var bottomBarsHeight: CGFloat = 0
}

extension View {
    /// Keeps a page's last rows clear of the bars over the bottom of its tab. For a tab's first page and every page
    /// pushed onto it: each is laid out on its own, so each needs telling.
    func roomForBottomBars() -> some View { modifier(BottomBarsRoom()) }
}

private struct BottomBarsRoom: ViewModifier {
    @Environment(\.bottomBarsHeight) private var height

    func body(content: Content) -> some View {
        content.safeAreaPadding(.bottom, height)
    }
}
