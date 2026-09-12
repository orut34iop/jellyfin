// SPDX-License-Identifier: MPL-2.0
// Menu functionality and official artwork originate from Jellyfin's macOS launcher.
import AppKit
import Foundation
import ServiceManagement

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    private let menu = NSMenu()
    private let stateItem = NSMenuItem(title: "Starting...", action: nil, keyEquivalent: "")
    private var startItem: NSMenuItem!
    private var stopItem: NSMenuItem!
    private var restartItem: NSMenuItem!
    private var loginItem: NSMenuItem!
    private var preferences: NSWindow?
    private var loginCheckbox: NSButton?
    private var instanceLock: InstanceLock?
    private var signalSource: DispatchSourceSignal?
    private var timer: Timer?
    private var ready = false
    private var polling = false
    private var quitting = false
    private var lastError: String?
    private let server = ServerController(executable: Bundle.main.bundleURL
        .appendingPathComponent("Contents/MacOS/launch-jellyfin"))
    private var version: String {
        let release = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "12.0.0"
        let stamp = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? ""
        return release + "-" + stamp
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        do {
            try FileManager.default.createDirectory(at: LauncherPaths.data.appendingPathComponent("log"),
                                                    withIntermediateDirectories: true)
            instanceLock = try InstanceLock(url: LauncherPaths.data.appendingPathComponent(".launcher.lock"))
        } catch {
            if case LauncherError.alreadyRunning = error {
                NSApp.terminate(nil)
                return
            }
            showError(error.localizedDescription)
            NSApp.terminate(nil)
            return
        }
        let log = LauncherPaths.data.appendingPathComponent("log/desktop-launcher.log")
        if !FileManager.default.fileExists(atPath: log.path) {
            FileManager.default.createFile(atPath: log.path, contents: nil)
        }
        server.output = try? FileHandle(forWritingTo: log)
        server.output?.seekToEndOfFile()

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let resource = Bundle.main.url(forResource: "StatusBarIcon", withExtension: "png"),
           let image = NSImage(contentsOf: resource) {
            image.size = NSSize(width: 18, height: 18)
            image.isTemplate = true
            statusItem.button?.image = image
        } else {
            statusItem.button?.title = "JF"
        }
        statusItem.button?.setAccessibilityLabel("Jellyfin V12")
        createMenu()
        server.onError = { [weak self] message in
            self?.lastError = message
            NSLog("%@", message)
        }
        server.onChange = { [weak self] in
            guard let self = self else { return }
            self.ready = false
            self.refreshMenu()
            if self.quitting && self.server.process == nil {
                NSApp.reply(toApplicationShouldTerminate: true)
            }
        }
        // Allow the installer and launchd to request a graceful server shutdown.
        signal(SIGTERM, SIG_IGN)
        signalSource = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        signalSource?.setEventHandler {
            RunLoop.main.perform { NSApp.terminate(nil) }
        }
        signalSource?.resume()
        server.start()
        timer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            self?.checkHealth()
        }
        checkHealth()
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard server.process != nil else { return .terminateNow }
        quitting = true
        server.stop()
        return .terminateLater
    }

    func applicationWillTerminate(_ notification: Notification) {
        timer?.invalidate()
        try? server.output?.close()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if server.process == nil { startServer() }
        showPreferences()
        return true
    }

    private func item(_ title: String, _ action: Selector, key: String = "") -> NSMenuItem {
        let entry = NSMenuItem(title: title, action: action, keyEquivalent: key)
        entry.target = self
        menu.addItem(entry)
        return entry
    }

    private func createMenu() {
        menu.autoenablesItems = false
        menu.delegate = self
        stateItem.isEnabled = false
        menu.addItem(stateItem)
        menu.addItem(.separator())
        _ = item("Open Jellyfin", #selector(openWeb), key: "l")
        if NSWorkspace.shared.urlForApplication(withBundleIdentifier: "tv.jellyfin.player") != nil {
            _ = item("Open Jellyfin Media Player", #selector(openPlayer))
        }
        _ = item("Show Logs", #selector(showLogs), key: "d")
        _ = item("Open Data Folder", #selector(showData))
        menu.addItem(.separator())
        startItem = item("Start Server", #selector(startServer))
        stopItem = item("Stop Server", #selector(stopServer))
        restartItem = item("Restart Server", #selector(restartServer), key: "r")
        menu.addItem(.separator())
        loginItem = item("Launch at Login", #selector(toggleLogin))
        _ = item("Preferences...", #selector(showPreferences), key: ",")
        _ = item("About Jellyfin V12", #selector(showAbout))
        menu.addItem(.separator())
        _ = item("Quit Jellyfin V12", #selector(quit), key: "q")
        statusItem.menu = menu
        refreshMenu()
    }

    func menuWillOpen(_ menu: NSMenu) { refreshMenu() }

    private func refreshMenu() {
        guard statusItem != nil else { return }
        let running = server.process != nil
        stateItem.title = server.stopping ? "Stopping safely..." :
            (running ? (ready ? "Server Running" : "Starting / Waiting for Server") : "Server Stopped")
        stateItem.toolTip = lastError
        startItem.isEnabled = !running && !quitting
        stopItem.isEnabled = running && !server.stopping && !quitting
        restartItem.isEnabled = !server.stopping && !quitting
        let status = SMAppService.mainApp.status
        loginItem.state = status == .enabled ? .on : (status == .requiresApproval ? .mixed : .off)
        loginItem.title = status == .requiresApproval ? "Launch at Login (Approval Required)" : "Launch at Login"
        loginCheckbox?.state = loginItem.state
        statusItem.button?.toolTip = "Jellyfin V12 \(version) - \(stateItem.title)"
        // A local diagnostic supports installation checks without changing preferences.
        let diagnostic: [String: Any] = [
            "version": version, "state": stateItem.title,
            "launcherPID": ProcessInfo.processInfo.processIdentifier,
            "serverPID": server.process?.processIdentifier ?? 0,
            "statusItemVisible": statusItem.isVisible,
            "menuItems": menu.items.filter { !$0.isSeparatorItem }.map { $0.title },
            "loginItemStatus": status.rawValue,
            "lastError": lastError ?? ""
        ]
        if let data = try? JSONSerialization.data(withJSONObject: diagnostic, options: [.prettyPrinted, .sortedKeys]) {
            try? data.write(to: LauncherPaths.data.appendingPathComponent("log/desktop-launcher-state.json"), options: .atomic)
        }
    }

    private func webURL() throws -> URL {
        let portFile = Bundle.main.url(forResource: "default-http-port", withExtension: nil)
        let text = portFile.flatMap { try? String(contentsOf: $0, encoding: .utf8) }
        let port = text.flatMap { Int($0.trimmingCharacters(in: .whitespacesAndNewlines)) } ?? 8096
        return try LauncherPaths.webURL(configuration: LauncherPaths.data.appendingPathComponent("config/network.xml"), defaultPort: port)
    }

    private func checkHealth() {
        guard let process = server.process, !server.stopping, !polling else { return }
        do {
            var url = try webURL()
            url.deleteLastPathComponent()
            url.deleteLastPathComponent()
            url.appendPathComponent("health")
            polling = true
            URLSession.shared.dataTask(with: URLRequest(url: url, timeoutInterval: 3)) { [weak self] data, response, error in
                DispatchQueue.main.async {
                    guard let self = self else { return }
                    self.polling = false
                    guard self.server.process === process, !self.server.stopping else { return }
                    self.ready = error == nil && (response as? HTTPURLResponse)?.statusCode == 200
                        && data.flatMap { String(data: $0, encoding: .utf8) } == "Healthy"
                    self.refreshMenu()
                }
            }.resume()
        } catch {
            lastError = error.localizedDescription
            refreshMenu()
        }
    }

    @objc private func startServer() { lastError = nil; server.start() }
    @objc private func stopServer() { server.stop() }
    @objc private func restartServer() { lastError = nil; server.stop(restart: true) }
    @objc private func quit() { NSApp.terminate(nil) }
    @objc private func openWeb() {
        do { NSWorkspace.shared.open(try webURL()) } catch { showError(error.localizedDescription) }
    }
    @objc private func showLogs() { NSWorkspace.shared.open(LauncherPaths.data.appendingPathComponent("log")) }
    @objc private func showData() { NSWorkspace.shared.open(LauncherPaths.data) }
    @objc private func openPlayer() {
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "tv.jellyfin.player") {
            NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration())
        }
    }

    @objc private func toggleLogin() {
        do {
            switch SMAppService.mainApp.status {
            case .enabled: try SMAppService.mainApp.unregister()
            case .requiresApproval: SMAppService.openSystemSettingsLoginItems()
            default: try SMAppService.mainApp.register()
            }
            refreshMenu()
            if SMAppService.mainApp.status == .requiresApproval {
                SMAppService.openSystemSettingsLoginItems()
            }
        } catch { showError("Unable to change Launch at Login: \(error.localizedDescription)") }
    }

    @objc private func showPreferences() {
        if preferences == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 380, height: 220),
                                  styleMask: [.titled, .closable], backing: .buffered, defer: false)
            window.title = "Jellyfin V12 Preferences"
            window.isReleasedWhenClosed = false
            let stack = NSStackView()
            stack.orientation = .vertical
            stack.alignment = .leading
            stack.spacing = 16
            stack.translatesAutoresizingMaskIntoConstraints = false
            stack.addArrangedSubview(NSTextField(labelWithString: "Jellyfin V12 \(version)"))
            let checkbox = NSButton(checkboxWithTitle: "Launch at Login", target: self, action: #selector(toggleLogin))
            loginCheckbox = checkbox
            stack.addArrangedSubview(checkbox)
            stack.addArrangedSubview(NSButton(title: "Open Jellyfin", target: self, action: #selector(openWeb)))
            stack.addArrangedSubview(NSButton(title: "Show Logs", target: self, action: #selector(showLogs)))
            window.contentView?.addSubview(stack)
            if let content = window.contentView {
                NSLayoutConstraint.activate([
                    stack.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 24),
                    stack.topAnchor.constraint(equalTo: content.topAnchor, constant: 24)
                ])
            }
            window.center()
            preferences = window
        }
        refreshMenu()
        NSApp.activate(ignoringOtherApps: true)
        preferences?.makeKeyAndOrderFront(nil)
    }

    @objc private func showAbout() {
        NSApp.activate(ignoringOtherApps: true)
        NSApp.orderFrontStandardAboutPanel(options: [
            .applicationName: "Jellyfin V12", .applicationVersion: version,
            .credits: NSAttributedString(string: "Jellyfin and Jellyfin Contributors\nhttps://jellyfin.org\nmacOS launcher: MPL-2.0")
        ])
    }

    private func showError(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "Jellyfin V12"
        alert.informativeText = message
        alert.alertStyle = .warning
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }
}

let application = NSApplication.shared
application.setActivationPolicy(.accessory)
let delegate = AppDelegate()
application.delegate = delegate
application.run()
