import Foundation
import Testing
@testable import FlexarrKit

struct ConcurrentMapTests {
    actor Counter {
        var inside = 0
        var most = 0
        func enter() { inside += 1; most = max(most, inside) }
        func leave() { inside -= 1 }
    }

    struct Failure: Error {}

    @Test func keepsTheOrderAndTheLimit() async throws {
        let counter = Counter()
        let numbers = Array(0..<30)
        let doubled = try await numbers.concurrentMap(limit: 4) { n in
            await counter.enter()
            // Later ones finish first, so order can't come from finishing order.
            try await Task.sleep(for: .milliseconds(30 - n))
            await counter.leave()
            return n * 2
        }
        #expect(doubled == numbers.map { $0 * 2 })
        let most = await counter.most
        #expect(most <= 4)
        #expect(most > 1)
    }

    @Test func emptyIsEmpty() async throws {
        let none: [Int] = []
        #expect(try await none.concurrentMap(limit: 4) { $0 }.isEmpty)
    }

    @Test func anErrorIsRethrown() async {
        await #expect(throws: Failure.self) {
            _ = try await [1, 2, 3].concurrentMap(limit: 2) { n in
                if n == 2 { throw Failure() }
                return n
            }
        }
    }
}
