import FlexarrKit
import SwiftUI

extension AppModel {
    /// The main profile may delete books (and edit them, on the web). Locked or not, as on the web.
    var canDeleteBooks: Bool { currentProfile?.isMain == true }
}

/// The ⋯ on a book's page: Delete… for the main profile, as the web's ⋯ has. It asks first, saying what goes (Flexarr
/// deletes the book's files itself), then leaves the page. A menu on the iPhone; on the TV a button that asks.
/// The caller styles the button.
struct BookMoreMenu: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let item: ItemSummary
    /// What deleting it does, once asked for: the question shows when it's here.
    @State private var plan: DeletionPlan?
    @State private var asking = false
    @State private var failure: String?
    #if os(tvOS)
    @State private var choosing = false
    #endif

    var body: some View {
        menu
            .accessibilityLabel(t("More actions"))
            .alert(t("Delete {title}?", ["title": item.title]), isPresented: $asking, presenting: plan) { _ in
                Button(t("Delete book"), role: .destructive) { Task { await delete() } }
                Button(t("Keep it"), role: .cancel) {}
            } message: { plan in
                Text(plan.bookMessage)
            }
            .alert(t("Couldn't delete it"), isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } })) {
                Button(t("OK"), role: .cancel) {}
            } message: {
                Text(failure ?? "")
            }
    }

    @ViewBuilder
    private var menu: some View {
        #if os(tvOS)
        Button { choosing = true } label: { Label(t("More"), systemImage: Symbol.more) }
            .confirmationDialog(item.title, isPresented: $choosing) {
                Button(t("Delete…"), role: .destructive) { Task { await ask() } }
            }
        #else
        Menu {
            Button(role: .destructive) { Task { await ask() } } label: { Label(t("Delete…"), systemImage: Symbol.delete) }
        } label: {
            Label(t("More"), systemImage: Symbol.more)
        }
        #endif
    }

    private func ask() async {
        guard let client = model.client else { return }
        do {
            plan = try await client.deletion(item.id)
            asking = true
        } catch {
            failure = ConnectionError(error).message
        }
    }

    private func delete() async {
        guard let client = model.client else { return }
        do {
            try await client.deleteItem(item.id)
            // Playing, it stops without sending its place back for a book that's gone. Only once it is gone: a delete
            // that fails leaves it playing, its place kept.
            model.music.discard(item.id)
            model.invalidate()
            dismiss()
        } catch {
            failure = ConnectionError(error).message
        }
    }
}
