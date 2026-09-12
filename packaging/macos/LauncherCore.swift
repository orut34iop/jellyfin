// SPDX-License-Identifier: MPL-2.0
// macOS launcher behavior adapted from jellyfin/jellyfin-server-macos.
import Foundation
import Darwin

enum LauncherError: LocalizedError {
    case invalidConfiguration(String)
    case alreadyRunning

    var errorDescription: String? {
        switch self {
        case .invalidConfiguration(let message): return message
        case .alreadyRunning: return "Jellyfin V12 is already running."
        }
    }
}

enum LauncherPaths {
    static let data = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/jellyfin-v12", isDirectory: true)

    static func webURL(configuration: URL, defaultPort: Int = 8096) throws -> URL {
        var values: [String: String] = [:]
        if FileManager.default.fileExists(atPath: configuration.path) {
            let document = try XMLDocument(contentsOf: configuration, options: .nodeLoadExternalEntitiesNever)
            guard let root = document.rootElement(), root.name == "NetworkConfiguration" else {
                throw LauncherError.invalidConfiguration("Invalid network.xml root. See the log folder.")
            }
            for node in root.children ?? [] {
                if let name = node.name {
                    values[name] = node.stringValue?.trimmingCharacters(in: .whitespacesAndNewlines)
                }
            }
        }
        let https = values["EnableHttps"]?.lowercased() == "true"
            && values["RequireHttps"]?.lowercased() == "true"
        let portText = values[https ? "InternalHttpsPort" : "InternalHttpPort"]
            ?? String(https ? 8920 : defaultPort)
        guard let port = Int(portText), (1...65535).contains(port) else {
            throw LauncherError.invalidConfiguration("Invalid HTTP/HTTPS port in network.xml.")
        }
        let base = (values["BaseUrl"] ?? "").trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        var components = URLComponents()
        components.scheme = https ? "https" : "http"
        components.host = "127.0.0.1"
        components.port = port
        components.path = (base.isEmpty ? "" : "/" + base) + "/web/index.html"
        guard let url = components.url else {
            throw LauncherError.invalidConfiguration("Unable to determine the Jellyfin web address.")
        }
        return url
    }
}

final class InstanceLock {
    private let descriptor: Int32

    init(url: URL) throws {
        descriptor = Darwin.open(url.path, O_CREAT | O_RDWR, 0o600)
        guard descriptor >= 0 else {
            throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
        }
        guard flock(descriptor, LOCK_EX | LOCK_NB) == 0 else {
            Darwin.close(descriptor)
            throw LauncherError.alreadyRunning
        }
        // A server child must not inherit the launcher's singleton lock.
        _ = fcntl(descriptor, F_SETFD, FD_CLOEXEC)
    }

    deinit { Darwin.close(descriptor) }
}

final class ServerController {
    private(set) var process: Process?
    private(set) var stopping = false
    private var restartRequested = false
    private let executable: URL
    private let arguments: [String]
    var onChange: (() -> Void)?
    var onError: ((String) -> Void)?
    var output: FileHandle?

    init(executable: URL, arguments: [String] = []) {
        self.executable = executable
        self.arguments = arguments
    }

    func start() {
        guard process == nil else { return }
        let task = Process()
        task.executableURL = executable
        task.arguments = arguments
        task.standardOutput = output
        task.standardError = output
        task.terminationHandler = { [weak self] task in
            // AppKit's terminateLater runs a nested event loop. Main-queue
            // dispatch can be blocked by the original termination request.
            RunLoop.main.perform(inModes: [.common, .default,
                RunLoop.Mode("NSModalPanelRunLoopMode"), RunLoop.Mode("NSEventTrackingRunLoopMode")]) {
                guard let self = self, self.process === task else { return }
                let restart = self.restartRequested
                let expected = self.stopping
                self.process = nil
                self.stopping = false
                self.restartRequested = false
                if !expected {
                    self.onError?("Server exited (status \(task.terminationStatus)). See Show Logs.")
                }
                if restart { self.start() } else { self.onChange?() }
            }
        }
        process = task
        do {
            try task.run()
        } catch {
            process = nil
            onError?("Unable to start server: \(error.localizedDescription)")
        }
        onChange?()
    }

    func stop(restart: Bool = false) {
        restartRequested = restart
        guard let task = process else {
            if restart { start() }
            return
        }
        stopping = true
        if task.isRunning { task.terminate() }
        onChange?()
    }
}
