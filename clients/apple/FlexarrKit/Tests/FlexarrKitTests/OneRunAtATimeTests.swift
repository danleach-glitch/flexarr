import Foundation
import Testing
@testable import FlexarrKit

@MainActor
struct OneRunAtATimeTests {
    @MainActor final class Counter {
        var runs = 0
        var overlapping = 0
        var inside = 0
    }

    @Test func runsDontOverlapAndCallsMeanwhileShareOneMoreRun() async {
        let runner = OneRunAtATime()
        let counter = Counter()
        let work: @MainActor () async -> Void = {
            counter.inside += 1
            if counter.inside > 1 { counter.overlapping += 1 }
            counter.runs += 1
            try? await Task.sleep(for: .milliseconds(50))
            counter.inside -= 1
        }
        let first = Task { await runner.run(work) }
        try? await Task.sleep(for: .milliseconds(10)) // the first run is under way
        await withTaskGroup(of: Void.self) { group in
            for _ in 0..<4 { group.addTask { await runner.run(work) } }
        }
        await first.value
        #expect(counter.overlapping == 0)
        // The first, then one more for every call that came while it ran.
        #expect(counter.runs == 2)
    }

    @Test func aCallAfterARunFinishesRunsAgain() async {
        let runner = OneRunAtATime()
        let counter = Counter()
        await runner.run { counter.runs += 1 }
        await runner.run { counter.runs += 1 }
        #expect(counter.runs == 2)
    }

    @Test func eachCallReturnsOnlyAfterARunThatBeganAfterIt() async {
        let runner = OneRunAtATime()
        var seen: [Int] = []
        var value = 0
        let first = Task { await runner.run { try? await Task.sleep(for: .milliseconds(50)); seen.append(value) } }
        try? await Task.sleep(for: .milliseconds(10))
        value = 1
        await runner.run { seen.append(value) }
        // The second call waited for the run after the first, which saw the newer value.
        #expect(seen.last == 1)
        await first.value
    }
}
