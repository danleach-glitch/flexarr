import Foundation

/// Work asked for from several places (a catch-up with the server, say) that runs one at a time: asked for while it
/// runs, it runs once more after, however many times it was asked meanwhile. Each `run` returns once a run that began
/// after it was asked has finished. Cancelling a caller cancels the run under way and that caller stops waiting once it
/// ends, but a run still wanted by others after it starts afresh, not cancelled.
@MainActor
public final class OneRunAtATime {
    /// The run under way, if any.
    private var running: Task<Void, Never>?
    /// Asked for while a run is under way: another follows it.
    private var wanted = false
    private var started = 0
    private var finished = 0

    public init() {}

    public func run(_ work: @escaping @MainActor () async -> Void) async {
        // The first run to begin from now on is the one this call waits for.
        let needed = started + 1
        wanted = true
        if running == nil { start(work) }
        while finished < needed, let run = running {
            await withTaskCancellationHandler { await run.value } onCancel: { run.cancel() }
            if Task.isCancelled { return }
        }
    }

    /// Each run is a task of its own, so a caller's cancelling one doesn't reach the next.
    private func start(_ work: @escaping @MainActor () async -> Void) {
        wanted = false
        started += 1
        running = Task { @MainActor in
            await work()
            self.finished += 1
            if self.wanted { self.start(work) } else { self.running = nil }
        }
    }
}
