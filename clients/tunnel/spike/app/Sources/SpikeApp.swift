import AVFoundation
import FlexarrTunnel
import SwiftUI

// Throwaway: proves the Go tsnet tunnel runs inside an iOS/tvOS app. Reads
// CONTROL_URL, TARGET and AUTHKEY from the environment (simctl launch passes
// SIMCTL_CHILD_*), starts the tunnel, fetches / and plays a video through it,
// printing SPIKE: lines, then exits.
@main
struct SpikeApp: App {
    init() { Task.detached { await runSpike() } }
    var body: some Scene { WindowGroup { Text("Tunnel spike") } }
}

func log(_ s: String) { print("SPIKE: \(s)"); fflush(stdout) }

func status() -> String {
    guard let p = FlexarrTunnelStatus() else { return "nil" }
    defer { FlexarrTunnelFree(p) }
    return String(cString: p)
}

func startTunnel(stateDir: String, host: String, key: String, target: String, control: String) -> (Int32, String?) {
    let args = [stateDir, host, key, target, control].map { strdup($0) }
    defer { args.forEach { free($0) } }
    let port = FlexarrTunnelStart(args[0], args[1], args[2], args[3], args[4])
    if port < 0, let e = FlexarrTunnelLastError() {
        defer { FlexarrTunnelFree(e) }
        return (port, String(cString: e))
    }
    return (port, nil)
}

func play(_ url: URL, label: String) async -> Bool {
    let item = AVPlayerItem(url: url)
    let player = AVPlayer(playerItem: item)
    player.isMuted = true
    player.play()
    for _ in 0..<60 {
        try? await Task.sleep(for: .milliseconds(500))
        if item.status == .failed {
            log("\(label) FAILED: \(item.error.map { "\($0)" } ?? "?")")
            return false
        }
        let t = player.currentTime().seconds
        if item.status == .readyToPlay, t > 2 {
            log("\(label) playing OK, currentTime=\(String(format: "%.1f", t))s duration=\(item.duration.seconds)")
            player.pause()
            return true
        }
    }
    log("\(label) TIMEOUT status=\(item.status.rawValue) t=\(player.currentTime().seconds)")
    return false
}

func runSpike() async {
    let env = ProcessInfo.processInfo.environment
    guard let control = env["CONTROL_URL"], let target = env["TARGET"] else {
        log("missing CONTROL_URL/TARGET"); return
    }
    let key = env["AUTHKEY"] ?? ""
    let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("tunnel").path
    if env["VERBOSE"] == "1" { FlexarrTunnelSetVerbose(1) }
    log("starting, stateDir=\(dir) target=\(target)")
    let t0 = Date()
    let (port, err) = await startTunnel(stateDir: dir, host: "spike-\(UIDevice.current.userInterfaceIdiom == .tv ? "tv" : "phone")",
                                  key: key, target: target, control: control)
    guard port > 0 else { log("start FAILED: \(err ?? "?") status=\(status())"); exit(1) }
    log("started on port \(port) in \(String(format: "%.2f", Date().timeIntervalSince(t0)))s status=\(status())")

    let base = URL(string: "http://127.0.0.1:\(port)")!
    do {
        let (data, resp) = try await URLSession.shared.data(from: base)
        log("GET / -> \((resp as? HTTPURLResponse)?.statusCode ?? 0) \(String(decoding: data, as: UTF8.self))")
    } catch {
        log("GET / FAILED: \(error)"); exit(1)
    }
    let mp4 = await play(base.appendingPathComponent("test.mp4"), label: "AVPlayer mp4")
    let hls = await play(base.appendingPathComponent("test.m3u8"), label: "AVPlayer hls")
    log("status=\(status())")
    FlexarrTunnelStop()
    log("stopped status=\(status())")
    log(mp4 && hls ? "RESULT PASS" : "RESULT FAIL")
    exit(mp4 && hls ? 0 : 1)
}
