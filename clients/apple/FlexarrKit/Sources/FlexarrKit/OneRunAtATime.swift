import Foundation

/// Work asked for from several places (a catch-up with the server, say) that runs one at a time: asked for while it
/// runs, it runs once more after, however many times it was asked meanwhile. Each `run` returns once a run that began
/// after it was asked has finished. Cancelling a caller cancels the run under way.
@MainActor
public final class OneRunAtATime {
    private var running: Task<Void, Never>?
    private var wanted = false

    public init() {}

    public func run(_ work: @escaping @MainActor () async -> Void) async {
        wanted = true
        let task: Task<Void, Never>
        if let running {
            task = running
        } else {
            task = Task { @MainActor in
                while self.wanted {
                    self.wanted = false
                    await work()
                }
                self.running = nil
            }
            running = task
        }
        await withTaskCancellationHandler { await task.value } onCancel: { task.cancel() }
    }
}
