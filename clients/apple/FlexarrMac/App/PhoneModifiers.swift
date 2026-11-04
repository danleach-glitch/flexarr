import SwiftUI

// The iPad screens (compiled in from FlexarriOS) use a few modifiers only iOS has. On the Mac they mean nothing,
// or the Mac's nearest: these stand in for them, so the screens are shared unchanged. SwiftUI's own versions are
// unavailable on macOS, so these are the ones chosen here.

enum PhoneTitleDisplayMode { case inline, large, automatic }
enum PhoneKeyboardType { case `default`, URL, numberPad, decimalPad, asciiCapable, emailAddress, numbersAndPunctuation, phonePad }
struct PhoneAutocapitalization: Sendable {
    static let never = PhoneAutocapitalization(), words = PhoneAutocapitalization()
    static let sentences = PhoneAutocapitalization(), characters = PhoneAutocapitalization()
}
enum PhonePageIndexDisplayMode { case always, never, automatic }

extension View {
    /// A title's size in the navigation bar: the Mac's window title is always the same.
    func navigationBarTitleDisplayMode(_ mode: PhoneTitleDisplayMode) -> some View { self }
    /// The on-screen keyboard's layout: a Mac has a real keyboard.
    func keyboardType(_ type: PhoneKeyboardType) -> some View { self }
    func textInputAutocapitalization(_ autocapitalization: PhoneAutocapitalization?) -> some View { self }
}

extension ToolbarItemPlacement {
    static var topBarTrailing: ToolbarItemPlacement { .primaryAction }
    static var topBarLeading: ToolbarItemPlacement { .navigation }
}

extension ToolbarPlacement {
    static var navigationBar: ToolbarPlacement { .windowToolbar }
    static var tabBar: ToolbarPlacement { .windowToolbar }
}

enum PhoneDrawerDisplayMode { case always, automatic }

extension SearchFieldPlacement {
    /// The search field under the navigation bar: the Mac's goes in the middle of the toolbar.
    static func navigationBarDrawer(displayMode: PhoneDrawerDisplayMode) -> SearchFieldPlacement { .toolbarPrincipal }
}

extension TabViewStyle where Self == DefaultTabViewStyle {
    /// Swiped pages: the Mac shows one at a time with its own Next button instead.
    static func page(indexDisplayMode: PhonePageIndexDisplayMode) -> DefaultTabViewStyle { .automatic }
}
