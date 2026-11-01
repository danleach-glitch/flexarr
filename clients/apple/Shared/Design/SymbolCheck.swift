import UIKit

extension Symbol {
    /// A debug build stops at launch if design/tokens.json names an SF Symbol this system doesn't have, since a wrong
    /// name otherwise just shows nothing.
    static func checkAll() {
        #if DEBUG
        for name in all where UIImage(systemName: name) == nil {
            assertionFailure("design/tokens.json names an SF Symbol this system doesn't have: \(name)")
        }
        #endif
    }
}
