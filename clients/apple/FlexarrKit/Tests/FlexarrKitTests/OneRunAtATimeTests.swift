import Foundation
import Testing
@testable import FlexarrKit

/// No timing guesses: each run waits for the test to let it finish, and the test waits until a run is under way, so
/// these hold however busy the machine is.
@MainActor
struct OneRunAtATimeTests {
    @MainActor final class Runs {
        var started = 0
        var completed = 0
        var cancelled = 0
        var inside = 0
        var overlapping = 0
        /// Runs may finish.
        var open = false

        /// A run: under way until the test opens the gate (or it's cancelled).
        func work() async {
            started += 1
            inside += 1
            if inside > 1 { overlapping += 1 }
            defer { inside -= 1 }
            while !open {
                if (try? await Task.sleep(for: .milliseconds(1))) == nil { cancelled += 1; return }
            }
            completed += 1
        }

        func waitUntil(_ condition: () -> Bool) async {
            while !condition() { try? await Task.sleep(for: .milliseconds(1)) }
        }
    }

    @Test func runsDontOverlapAndCallsMeanwhileShareOneMoreRun() async {
        let runner = OneRunAtATime()
        let runs = Runs()
        let first = Task { await runner.run(runs.work) }
        await runs.waitUntil { runs.started == 1 }
        let others = (0..<4).map { _ in Task { await runner.run(runs.work) } }
        for _ in 0..<20 { await Task.yield() } // the four calls are made
        runs.open = true
        await first.value
        for other in others { await other.value }
        #expect(runs.overlapping == 0)
        // The first, then one more for every call that came while it ran.
        #expect(runs.started == 2)
        #expect(runs.completed == 2)
    }

    @Test func aCallAfterARunFinishesRunsAgain() async {
        let runner = OneRunAtATime()
        var count = 0
        await runner.run { count += 1 }
        await runner.run { count += 1 }
        #expect(count == 2)
    }

    @Test func eachCallReturnsOnlyAfterARunThatBeganAfterIt() async {
        let runner = OneRunAtATime()
        let runs = Runs()
        let first = Task { await runner.run(runs.work) }
        await runs.waitUntil { runs.started == 1 }
        let second = Task { await runner.run(runs.work) }
        for _ in 0..<20 { await Task.yield() }
        runs.open = true
        await second.value
        // The second call came back only after the run that began after it.
        #expect(runs.started == 2)
        #expect(runs.completed == 2)
        await first.value
    }

    /// A caller cancelled (background time running out, say) stops the run under way and stops waiting, but a run
    /// another caller asked for meanwhile still happens, and isn't cancelled.
    @Test func cancellingACallerLeavesTheFollowingRunAlone() async {
        let runner = OneRunAtATime()
        let runs = Runs()
        let first = Task { await runner.run(runs.work) }
        await runs.waitUntil { runs.started == 1 }
        let second = Task { await runner.run(runs.work) }
        for _ in 0..<20 { await Task.yield() } // the second call is made
        first.cancel()
        await first.value
        #expect(runs.cancelled == 1)
        #expect(runs.completed == 0) // the cancelled caller didn't wait for the run after
        await runs.waitUntil { runs.started == 2 }
        runs.open = true
        await second.value
        #expect(runs.completed == 1)
        #expect(runs.cancelled == 1)
    }
}
