import SwiftUI

extension EnvironmentValues {
    /// Set on Home's Continue Watching row: a card's long-press menu can take a show's next episode out of it.
    @Entry var inContinueWatching = false
    /// Set by a Home row: told a card's id just before a menu action that may take it out of the row, so focus can go
    /// to the card that takes its place (tvOS, left to itself, moves it to a neighbour or the row's start).
    @Entry var leavesRow: ((String) -> Void)? = nil
}
