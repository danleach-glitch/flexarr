#if os(iOS)
import CoreMotion
import Foundation

/// Notices the phone being shaken, for "shake to add time" while a sleep timer runs. Listens to the accelerometer
/// only while `isActive`, so it costs nothing the rest of the time.
@MainActor
final class ShakeDetector {
    var onShake: (() -> Void)?
    var isActive = false {
        didSet { if isActive != oldValue { isActive ? start() : stop() } }
    }

    private let motion = CMMotionManager()
    private var lastShake = Date.distantPast
    /// Strong jolts in the last moment: a shake is a few of them, not one bump.
    private var jolts: [Date] = []

    private func start() {
        guard motion.isAccelerometerAvailable, !motion.isAccelerometerActive else { return }
        motion.accelerometerUpdateInterval = 1.0 / 20
        motion.startAccelerometerUpdates(to: .main) { [weak self] data, _ in
            guard let data else { return }
            let force = sqrt(data.acceleration.x * data.acceleration.x + data.acceleration.y * data.acceleration.y + data.acceleration.z * data.acceleration.z)
            MainActor.assumeIsolated { self?.felt(force) }
        }
    }

    private func stop() {
        motion.stopAccelerometerUpdates()
        jolts = []
    }

    private func felt(_ force: Double) {
        guard force > 2.2 else { return }
        let now = Date()
        jolts = jolts.filter { now.timeIntervalSince($0) < 0.8 } + [now]
        guard jolts.count >= 3, now.timeIntervalSince(lastShake) > 2 else { return }
        lastShake = now
        jolts = []
        onShake?()
    }
}
#endif
