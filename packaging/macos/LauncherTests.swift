// SPDX-License-Identifier: MPL-2.0
import Foundation

@main
struct LauncherTests {
    static func main() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let config = root.appendingPathComponent("network.xml")
        func check(_ condition: @autoclosure () -> Bool, _ message: String) {
            precondition(condition(), message)
            print("PASS: " + message)
        }
        func write(_ xml: String) throws {
            try ("<NetworkConfiguration>" + xml + "</NetworkConfiguration>").write(to: config, atomically: true, encoding: .utf8)
        }
        func waitFor(_ condition: () -> Bool) {
            let deadline = Date().addingTimeInterval(5)
            while !condition() && Date() < deadline {
                RunLoop.current.run(until: Date().addingTimeInterval(0.02))
            }
            precondition(condition(), "Timed out waiting for process lifecycle")
        }
        let initial = try LauncherPaths.webURL(configuration: config)
        check(initial.absoluteString == "http://127.0.0.1:8096/web/index.html", "default port is 8096")
        try write("<InternalHttpPort>9000</InternalHttpPort><BaseUrl>/media/</BaseUrl>")
        let custom = try LauncherPaths.webURL(configuration: config)
        check(custom.absoluteString == "http://127.0.0.1:9000/media/web/index.html", "custom port and base URL")
        try write("<EnableHttps>true</EnableHttps><RequireHttps>true</RequireHttps><InternalHttpsPort>9443</InternalHttpsPort>")
        let https = try LauncherPaths.webURL(configuration: config)
        check(https.absoluteString == "https://127.0.0.1:9443/web/index.html", "required HTTPS")
        try write("<EnableHttps>true</EnableHttps><RequireHttps>false</RequireHttps>")
        let optional = try LauncherPaths.webURL(configuration: config)
        check(optional.scheme == "http", "optional HTTPS keeps HTTP usable")
        try write("<InternalHttpPort>99999</InternalHttpPort>")
        do {
            _ = try LauncherPaths.webURL(configuration: config)
            preconditionFailure("Invalid port accepted")
        } catch { print("PASS: invalid configuration rejected") }
        try "<broken".write(to: config, atomically: true, encoding: .utf8)
        do {
            _ = try LauncherPaths.webURL(configuration: config)
            preconditionFailure("Malformed XML accepted")
        } catch { print("PASS: malformed XML rejected") }
        let lockURL = root.appendingPathComponent("instance.lock")
        var lock: InstanceLock? = try InstanceLock(url: lockURL)
        withExtendedLifetime(lock) {
            do {
                _ = try InstanceLock(url: lockURL)
                preconditionFailure("Duplicate instance accepted")
            } catch { print("PASS: duplicate launcher blocked") }
        }
        lock = nil
        _ = try InstanceLock(url: lockURL)
        print("PASS: instance lock released")

        let server = ServerController(executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["60"])
        server.start()
        let first = server.process!.processIdentifier
        server.start()
        check(server.process!.processIdentifier == first, "duplicate start does not spawn a server")
        server.stop(restart: true)
        waitFor { server.process != nil && server.process!.processIdentifier != first }
        check(server.process!.isRunning, "restart waits for old process to exit")
        server.stop()
        waitFor { server.process == nil }
        check(!server.stopping, "stop reaps server and clears state")
        let missing = ServerController(executable: root.appendingPathComponent("missing"))
        var failed = false
        missing.onError = { _ in failed = true }
        missing.start()
        check(failed && missing.process == nil, "launch failure is reported without a phantom process")
        var nestedCompleted = false
        DispatchQueue.main.async {
            let nested = ServerController(executable: URL(fileURLWithPath: "/bin/sleep"), arguments: ["60"])
            nested.start()
            nested.stop()
            // Reproduce AppKit waiting inside a main-queue termination callback.
            waitFor { nested.process == nil }
            check(!nested.stopping, "shutdown completes inside a nested main-queue event loop")
            nestedCompleted = true
        }
        waitFor { nestedCompleted }
        print("13 launcher checks passed")
    }
}
