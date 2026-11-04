import FlexarrKit
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

    /// A search field for the page, on the Mac: at the toolbar's right, after its actions, the tabs' height.
    @ViewBuilder
    func pageSearch(_ text: Binding<String>, prompt: String) -> some View {
        #if os(macOS)
        toolbar {
            if #available(macOS 26, *) {
                ToolbarSpacer(.flexible)
                ToolbarItem(placement: .automatic) { PageSearchField(text: text, prompt: prompt) }
                    .sharedBackgroundVisibility(.hidden)
            } else {
                ToolbarItem(placement: .automatic) { PageSearchField(text: text, prompt: prompt) }
            }
        }
        #else
        self
        #endif
    }

    /// The Mac's back button, for a page pushed onto a stack: the tabs' capsule, in from the window's corner.
    @ViewBuilder
    func macBackButton() -> some View {
        #if os(macOS)
        navigationBarBackButtonHidden(true)
            .toolbar {
                if #available(macOS 26, *) {
                    ToolbarItem(placement: .navigation) { PageBackButton() }
                        .sharedBackgroundVisibility(.hidden)
                } else {
                    ToolbarItem(placement: .navigation) { PageBackButton() }
                }
            }
        #else
        self
        #endif
    }
}

#if os(macOS)
/// The page's controls share one height and one capsule (docs/mac-player.md): tabs, search fields and back.
enum PageControl {
    static let height: CGFloat = 34
    static var track: some ShapeStyle { Color.seat.opacity(0.9) }
    static var edge: some ShapeStyle { Color.screen.opacity(0.08) }
}

/// Back, as a capsule the tabs' height.
struct PageBackButton: View {
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        Button { dismiss() } label: {
            Image(systemName: "chevron.backward")
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(Color.screen)
                .frame(width: 44, height: PageControl.height)
                .background(Capsule().fill(PageControl.track))
                .overlay(Capsule().strokeBorder(PageControl.edge))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .keyboardShortcut("[", modifiers: .command)
        .help(t("Back"))
        .accessibilityLabel(t("Back"))
        .padding(.leading, 8)
    }
}

/// A search field the tabs' height, with its magnifier and a clear button.
struct PageSearchField: View {
    @Binding var text: String
    let prompt: String
    var width: CGFloat = 260
    /// The page's own focus for the field (Search puts the cursor in it on arrival), else the field's.
    var focus: FocusState<Bool>.Binding?
    /// Return in the field.
    var onSubmit: () -> Void = {}
    @FocusState private var ownFocus: Bool
    @Environment(Router.self) private var router: Router?
    @State private var depth: Int?

    private var focused: FocusState<Bool>.Binding { focus ?? $ownFocus }

    /// The fields on screen, by their place's stack and depth in it, for ⌘F (MacNavigation.findOnPage). Not
    /// observed: only read when ⌘F is pressed.
    @MainActor static var registered: [(router: ObjectIdentifier, depth: Int)] = []

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").font(.system(size: 13, weight: .semibold)).foregroundStyle(Color.usher)
            TextField(prompt, text: $text)
                .textFieldStyle(.plain)
                .font(.system(size: 14))
                .focused(focused)
                .onSubmit(onSubmit)
            if !text.isEmpty {
                Button { text = "" } label: {
                    Image(systemName: "xmark.circle.fill").font(.system(size: 13)).foregroundStyle(Color.usher)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(t("Clear"))
            }
        }
        .padding(.horizontal, 14)
        .frame(width: width, height: PageControl.height)
        .background(Capsule().fill(PageControl.track))
        .overlay(Capsule().strokeBorder(focused.wrappedValue ? AnyShapeStyle(Color.silver.opacity(0.6)) : AnyShapeStyle(PageControl.edge)))
        .onAppear {
            guard let router else { return }
            depth = router.path.count
            Self.registered.append((ObjectIdentifier(router), router.path.count))
        }
        .onDisappear {
            guard let router, let depth else { return }
            if let at = Self.registered.firstIndex(where: { $0.router == ObjectIdentifier(router) && $0.depth == depth }) {
                Self.registered.remove(at: at)
            }
        }
        .onChange(of: MacNavigation.shared.find?.count) { _, _ in
            guard let router, let depth, let find = MacNavigation.shared.find,
                  find.router == ObjectIdentifier(router), find.depth == depth else { return }
            focused.wrappedValue = true
        }
    }
}

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
                        .frame(height: PageControl.height)
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
        .background(Capsule().fill(PageControl.track))
        .overlay(Capsule().strokeBorder(PageControl.edge))
        .fixedSize()
    }
}
#endif
