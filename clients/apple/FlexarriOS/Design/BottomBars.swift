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

    /// A page pushed onto a tab: on iPad a steady small title, as the tab bar's along the top there. Left automatic, a
    /// pushed page's large title started collapsed and jumped to large (moving the page down) at the first touch.
    /// Deeper pages left automatic inherit it. The tabs' first pages keep their large titles, and the iPhone is as it was.
    func pushedPageTitle() -> some View { modifier(PushedPageTitle()) }
}

private struct BottomBarsRoom: ViewModifier {
    @Environment(\.bottomBarsHeight) private var height

    func body(content: Content) -> some View {
        content.safeAreaPadding(.bottom, height)
    }
}

private struct PushedPageTitle: ViewModifier {
    @Environment(\.horizontalSizeClass) private var sizeClass

    func body(content: Content) -> some View {
        #if os(iOS)
        content.navigationBarTitleDisplayMode(sizeClass == .regular ? .inline : .automatic)
        #else
        content
        #endif
    }
}
