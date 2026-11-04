import SwiftUI

// Where a page's own controls go (docs/mac-player.md, "The Mac's page layout"): its view switch (Browse, All, Genres…)
// and its actions (filter, sort, Recordings, Guide). On the iPhone the switch stays in the page and the actions go at
// the top right of the navigation bar. On the Mac the switch is in the middle of the window's toolbar, the same size
// on every page, and the actions at its right as named buttons. The Apple TV draws its own and ignores these.

extension View {
    /// The page's view switch, on the Mac: in the middle of the toolbar, as `PageTabs`. Nothing when there's one view
    /// or none.
    @ViewBuilder
    func pageSwitch<Value: Hashable>(_ selection: Binding<Value>, _ options: [(Value, String)]) -> some View {
        #if os(macOS)
        toolbar {
            if options.count > 1 {
                if #available(macOS 26, *) {
                    // Its own track: not inside the toolbar's glass as well.
                    ToolbarItem(placement: .principal) { PageTabs(selection: selection, options: options) }
                        .sharedBackgroundVisibility(.hidden)
                } else {
                    ToolbarItem(placement: .principal) { PageTabs(selection: selection, options: options) }
                }
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

#if os(macOS)
/// The Mac's page tabs (Browse, All movies, Genres…): a capsule track with the chosen one filled to its edges, the same
/// size on every page. The system's segmented control insets its highlight from the track, which reads as unfinished
/// at this size.
struct PageTabs<Value: Hashable>: View {
    @Binding var selection: Value
    let options: [(Value, String)]
    @Namespace private var pill

    var body: some View {
        HStack(spacing: 0) {
            ForEach(Array(options.enumerated()), id: \.offset) { _, option in
                let chosen = option.0 == selection
                Button {
                    withAnimation(.snappy(duration: 0.25)) { selection = option.0 }
                } label: {
                    Text(option.1)
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(chosen ? Color.room : Color.screen.opacity(0.85))
                        .padding(.horizontal, 18)
                        .frame(height: 34)
                        .background {
                            if chosen {
                                Capsule().fill(Color.screen).matchedGeometryEffect(id: "pill", in: pill)
                            }
                        }
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(chosen ? [.isSelected] : [])
            }
        }
        .background(Capsule().fill(Color.seat.opacity(0.9)))
        .overlay(Capsule().strokeBorder(Color.screen.opacity(0.08)))
        .fixedSize()
    }
}
#endif
