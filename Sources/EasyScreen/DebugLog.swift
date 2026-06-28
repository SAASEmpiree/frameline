import AppKit

/// JOURNALISATION DE DEBUG (temporaire) pour traquer le « retour / réouverture de fenêtre » lors des
/// captures. Écrit dans ~/Library/Logs/EasyScreen-debug.log (+ NSLog "ESLOG ..."). Observateurs
/// système posés au lancement : changement de Space, activation d'app, fenêtre devenant key.
/// → À RETIRER une fois le bug compris.
enum ESLog {
    static let url = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Logs/EasyScreen-debug.log")
    private static let q = DispatchQueue(label: "com.saasempiree.eslog")
    private static var configured = false
    private static let fmt: DateFormatter = {
        let f = DateFormatter(); f.dateFormat = "HH:mm:ss.SSS"; return f
    }()

    static func log(_ msg: String) {
        let line = "[\(fmt.string(from: Date()))] \(msg)\n"
        NSLog("ESLOG %@", msg)
        let data = Data(line.utf8)
        q.async {
            if let h = try? FileHandle(forWritingTo: url) {
                h.seekToEndOfFile(); h.write(data); try? h.close()
            } else {
                try? data.write(to: url)
            }
        }
    }

    /// Photo de l'état d'activation/fenêtres au moment `tag`.
    @MainActor
    static func snapshot(_ tag: String) {
        let front = NSWorkspace.shared.frontmostApplication
        let policy = NSApp.activationPolicy().rawValue          // 0=regular 1=accessory 2=prohibited
        let active = NSApp.isActive
        let vis = NSApp.windows.filter { $0.isVisible }
            .map { "\(type(of: $0))(key=\($0.isKeyWindow),lvl=\($0.level.rawValue),onActiveSpace=\($0.isOnActiveSpace))" }
            .joined(separator: " ")
        log("\(tag) | front=\(front?.localizedName ?? "?")(\(front?.processIdentifier ?? -1)) selfActive=\(active) policy=\(policy) vis=[\(vis)]")
    }

    /// Empile quelques frames de pile d'appel (pour savoir QUI appelle un point sensible).
    static func stack(_ tag: String, depth: Int = 10) {
        let frames = Thread.callStackSymbols.dropFirst().prefix(depth).joined(separator: "\n    ")
        log("\(tag) — call stack:\n    \(frames)")
    }

    @MainActor
    static func installObservers() {
        guard !configured else { return }
        configured = true
        try? Data("=== EasyScreen debug log ===\n".utf8).write(to: url)
        let nc = NotificationCenter.default
        nc.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { snapshot("APP didBecomeActive") }
        }
        nc.addObserver(forName: NSApplication.didResignActiveNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { snapshot("APP didResignActive") }
        }
        nc.addObserver(forName: NSWindow.didBecomeKeyNotification, object: nil, queue: .main) { note in
            MainActor.assumeIsolated {
                let w = note.object.map { String(describing: type(of: $0)) } ?? "?"
                snapshot("WINDOW didBecomeKey \(w)")
            }
        }
        let wnc = NSWorkspace.shared.notificationCenter
        wnc.addObserver(forName: NSWorkspace.activeSpaceDidChangeNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { snapshot(">>> SPACE CHANGED <<<") }
        }
        wnc.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { note in
            MainActor.assumeIsolated {
                let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
                snapshot("WS didActivateApp=\(app?.localizedName ?? "?")")
            }
        }
        log("observers installed → log file: \(url.path)")
    }
}
