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
    /// On iPhone a list or grid (`large`: a library, Watchlist, a Settings page) says large, so it's the same size
    /// however it was reached and after the phone's been turned; a title's own page keeps the small title it asks for.
    func pushedPageTitle(large: Bool = false) -> some View { modifier(PushedPageTitle(large: large)) }

    /// A large iPhone held sideways reports a regular width but a compact height: the pages under this see a compact
    /// width there, so their grids, rows and covers keep the phone's sizes and fit the short screen. Only an iPad (whose
    /// height is always regular) gets the big ones.
    func phoneSizesWhenSideways() -> some View { modifier(PhoneSizesWhenSideways()) }
}

private struct PhoneSizesWhenSideways: ViewModifier {
    #if os(iOS)
    @Environment(\.horizontalSizeClass) private var horizontal
    @Environment(\.verticalSizeClass) private var vertical
    #endif

    func body(content: Content) -> some View {
        #if os(iOS)
        content.environment(\.horizontalSizeClass, vertical == .compact ? .compact : horizontal)
        #else
        content
        #endif
    }
}

private struct BottomBarsRoom: ViewModifier {
    @Environment(\.bottomBarsHeight) private var height

    func body(content: Content) -> some View {
        content.safeAreaPadding(.bottom, height)
    }
}

private struct PushedPageTitle: ViewModifier {
    let large: Bool
    #if os(iOS)
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.verticalSizeClass) private var vertical
    #endif

    func body(content: Content) -> some View {
        #if os(iOS)
        // Only an iPad (always a regular height) is wide here: a large iPhone held sideways is still a phone, so its
        // titles don't change size as it turns.
        let iPad = sizeClass == .regular && vertical == .regular
        content.navigationBarTitleDisplayMode(iPad ? .inline : large ? .large : .automatic)
        #else
        content
        #endif
    }
}
