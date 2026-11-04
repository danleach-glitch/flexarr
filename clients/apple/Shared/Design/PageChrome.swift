import SwiftUI

// Where a page's own controls go (docs/mac-player.md, "The Mac's page layout"): its view switch (Browse, All, Genres…)
// and its actions (filter, sort, Recordings, Guide). On the iPhone the switch stays in the page and the actions go at
// the top right of the navigation bar. On the Mac the switch is in the middle of the window's toolbar, the same size
// on every page, and the actions at its right as named buttons. The Apple TV draws its own and ignores these.

extension View {
    /// The page's view switch, on the Mac: in the middle of the toolbar.
    @ViewBuilder
    func pageSwitch<Switch: View>(@ViewBuilder _ content: () -> Switch) -> some View {
        #if os(macOS)
        let control = content()
        toolbar {
            ToolbarItem(placement: .principal) {
                control
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .controlSize(.large)
                    .fixedSize()
            }
        }
        #else
        self
        #endif
    }

    /// The page's actions: at the top right on the iPhone; at the toolbar's right on the Mac, each named beside its
    /// icon so it reads as a button.
    @ViewBuilder
    func pageActions<Actions: View>(@ViewBuilder _ content: () -> Actions) -> some View {
        #if os(macOS)
        let actions = content()
        toolbar {
            if #available(macOS 26, *) {
                ToolbarSpacer(.flexible)
            }
            ToolbarItemGroup(placement: .automatic) {
                actions
                    .labelStyle(.titleAndIcon)
                    .controlSize(.large)
            }
        }
        #elseif os(iOS)
        let actions = content()
        toolbar {
            ToolbarItemGroup(placement: .topBarTrailing) { actions }
        }
        #else
        self
        #endif
    }
}
