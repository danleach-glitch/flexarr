import Foundation
import Observation

/// Runs the server bundled in Contents/Helpers as a child process, which inherits the app's sandbox and the
/// folders it has open, even ones chosen while it runs. Restarts it if it stops on its own.
@MainActor
@Observable
final class ServerProcess {
    enum Status: Equatable, CustomStringConvertible {
        case stopped, starting, running
        case failed(String)

        var description: String {
            switch self {
            case .stopped: "Flexarr is stopped"
            case .starting: "Flexarr is starting…"
            case .running: "Flexarr is running"
            case .failed(let reason): "Flexarr stopped: \(reason)"
            }
        }
    }

    private(set) var status = Status.stopped
    /// While nobody has set the server up: the code its welcome page asks for, which it keeps in its data folder.
    private(set) var setupCode: String?
    private let requests: AppRequests
    private var requestsPort: UInt16?
    private var process: Process?
    private var quickExits = 0

    /// The port the server listens on. `defaults write <bundle id> Port 8484` changes it.
    let port: Int = UserDefaults.standard.integer(forKey: "Port").nonZero ?? 8383
    var address: URL { URL(string: "http://localhost:\(port)")! }

    /// Application Support inside the app's container.
    private let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appending(path: "Flexarr")
    var logDirectory: URL { root.appending(path: "logs") }
    private var setupCodeFile: URL { root.appending(path: "data/setup-code.txt") }
    /// The server's settings, API key included: only this app (and whoever is signed in to this Mac) can read them.
    var configFile: URL { root.appending(path: "data/config.json") }

    init(folders: MediaFolders) {
        requests = AppRequests(folders: folders)
    }

    func start() {
        guard process == nil else { return }
        // The server asks the app for folders, so the app listens before the server starts.
        guard let requestsPort else {
            status = .starting
            Task {
                do {
                    requestsPort = try await requests.start()
                    start()
                } catch {
                    status = .failed(error.localizedDescription)
                }
            }
            return
        }
        // The executable and its libraries are in Contents/Helpers, one server for each kind of Mac
        // (Tools/publish-server.sh); its settings and the web app in Resources/Server.
        #if arch(arm64)
        let executable = Bundle.main.bundleURL.appending(path: "Contents/Helpers/Flexarr-arm64")
        #else
        let executable = Bundle.main.bundleURL.appending(path: "Contents/Helpers/Flexarr-x64")
        #endif
        let content = Bundle.main.resourceURL!.appending(path: "Server")
        let data = root.appending(path: "data")
        try? FileManager.default.createDirectory(at: data, withIntermediateDirectories: true)
        try? FileManager.default.createDirectory(at: logDirectory, withIntermediateDirectories: true)

        let process = Process()
        process.executableURL = executable
        process.currentDirectoryURL = content
        var environment = ProcessInfo.processInfo.environment
        environment["ASPNETCORE_ENVIRONMENT"] = "Production"
        environment["ASPNETCORE_CONTENTROOT"] = content.path
        environment["Urls"] = "http://*:\(port)"
        environment["Flexarr__DataDirectory"] = data.path
        environment["Flexarr__Logs__Directory"] = logDirectory.path
        environment["Flexarr__Logs__ConsoleLevel"] = "Warning"
        // The app's own FFmpeg (Tools/build-ffmpeg.sh), beside the server.
        environment["Flexarr__FfmpegPath"] = executable.deletingLastPathComponent().appending(path: "ffmpeg").path
        environment["Flexarr__FfprobePath"] = executable.deletingLastPathComponent().appending(path: "ffprobe").path
        environment["Flexarr__MacApp__Port"] = String(requestsPort)
        environment["Flexarr__MacApp__Token"] = requests.token
        process.environment = environment

        let console = logDirectory.appending(path: "console.log")
        if !FileManager.default.fileExists(atPath: console.path) {
            FileManager.default.createFile(atPath: console.path, contents: nil)
        }
        if let handle = try? FileHandle(forWritingTo: console) {
            handle.seekToEndOfFile()
            process.standardOutput = handle
            process.standardError = handle
        }

        let started = Date()
        process.terminationHandler = { [weak self] ended in
            let code = ended.terminationStatus
            let which = ObjectIdentifier(ended)
            Task { @MainActor in self?.ended(which, code: code, after: Date().timeIntervalSince(started)) }
        }

        do {
            try process.run()
        } catch {
            status = .failed(error.localizedDescription)
            return
        }
        self.process = process
        status = .starting
        Task { await waitUntilAnswering() }
    }

    /// Asks the server to stop and waits for it, up to 10 seconds, then kills it: Quit mustn't leave it running, and
    /// a server slow to shut down mustn't hang the menu.
    func stop() {
        guard let process else { return }
        // Forgotten first, so its exit isn't taken for a crash to restart from.
        self.process = nil
        process.terminate()
        let deadline = Date().addingTimeInterval(10)
        while process.isRunning && Date() < deadline {
            Thread.sleep(forTimeInterval: 0.05)
        }
        if process.isRunning {
            kill(process.processIdentifier, SIGKILL)
            process.waitUntilExit()
        }
        status = .stopped
    }

    func restart() {
        stop()
        quickExits = 0
        start()
    }

    /// A server exited. Only the one running now counts: one that was stopped (by Stop, Restart or Quit) is done with.
    private func ended(_ which: ObjectIdentifier, code: Int32, after seconds: TimeInterval) {
        guard let process, ObjectIdentifier(process) == which else { return }
        self.process = nil
        // Give up after a few exits in a row within seconds of starting: something's wrong that a restart won't fix.
        quickExits = seconds < 30 ? quickExits + 1 : 0
        if quickExits >= 3 {
            status = .failed("it exited with code \(code). See the logs.")
            return
        }
        status = .starting
        Task {
            try? await Task.sleep(for: .seconds(2))
            // Unless it was stopped in the meantime.
            if status == .starting { start() }
        }
    }

    private func waitUntilAnswering() async {
        let health = address.appending(path: "health")
        for _ in 0..<120 {
            guard process != nil else { return }
            if let (_, response) = try? await URLSession.shared.data(from: health),
               (response as? HTTPURLResponse)?.statusCode == 200 {
                status = .running
                await followSetupCode()
                return
            }
            try? await Task.sleep(for: .milliseconds(500))
        }
    }
}

extension ServerProcess {
    /// Shows the setup code while the file is there: the server deletes it once someone has set it up.
    fileprivate func followSetupCode() async {
        while true {
            let code = (try? String(contentsOf: setupCodeFile, encoding: .utf8))?.trimmingCharacters(in: .whitespacesAndNewlines)
            setupCode = code?.isEmpty == false ? code : nil
            guard setupCode != nil, process != nil else { return }
            try? await Task.sleep(for: .seconds(5))
        }
    }
}

private extension Int {
    var nonZero: Int? { self == 0 ? nil : self }
}
