import AppKit

/// Icône + menu dans la barre des menus. Les actions sont déléguées via des closures.
final class StatusBarController: NSObject {
    private let statusItem: NSStatusItem
    private let menu = NSMenu()
    private var recordItem: NSMenuItem!

    var onOpenWindow: (() -> Void)?
    var onRegion, onWindow, onFull, onRegionClipboard: (() -> Void)?
    var onToggleRecord, onRecordRegion, onOpenFolder, onPreferences, onAbout: (() -> Void)?

    override init() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()
        if let button = statusItem.button {
            let img = NSImage(systemSymbolName: "camera.viewfinder", accessibilityDescription: "Easy Screen")
            img?.isTemplate = true
            button.image = img
        }
        buildMenu()
        statusItem.menu = menu
        NotificationCenter.default.addObserver(self, selector: #selector(updateRecordTitle),
                                               name: .recordingStateChanged, object: nil)
    }

    private func add(_ title: String, _ selector: Selector, hint: String = "") {
        let item = NSMenuItem(title: hint.isEmpty ? title : "\(title)   \(hint)",
                              action: selector, keyEquivalent: "")
        item.target = self
        menu.addItem(item)
    }

    private func buildMenu() {
        add("Ouvrir Easy Screen", #selector(openWindow))
        menu.addItem(.separator())
        add("Capturer une zone", #selector(region), hint: CaptureHotkey.label(for: CaptureHotkey.region))
        add("Capturer une fenêtre", #selector(window), hint: CaptureHotkey.label(for: CaptureHotkey.window))
        add("Capturer tout l'écran", #selector(full), hint: CaptureHotkey.label(for: CaptureHotkey.fullScreen))
        menu.addItem(.separator())
        add("Copier une zone (presse-papiers)", #selector(regionClip))
        menu.addItem(.separator())

        recordItem = NSMenuItem(title: "Démarrer l'enregistrement   \(CaptureHotkey.label(for: CaptureHotkey.record))",
                                action: #selector(toggleRecord), keyEquivalent: "")
        recordItem.target = self
        menu.addItem(recordItem)
        add("Enregistrer une zone…", #selector(recordRegion))
        menu.addItem(.separator())

        add("Ouvrir le dossier des captures", #selector(openFolder))
        let prefs = NSMenuItem(title: "Réglages…", action: #selector(preferences), keyEquivalent: ",")
        prefs.keyEquivalentModifierMask = [.command]
        prefs.target = self
        menu.addItem(prefs)
        menu.addItem(.separator())

        add("À propos d'Easy Screen", #selector(about))
        // target nil → l'action remonte la chaîne des responders jusqu'à NSApp.
        let quit = NSMenuItem(title: "Quitter Easy Screen",
                              action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        quit.keyEquivalentModifierMask = [.command]
        menu.addItem(quit)

        updateRecordTitle()
    }

    @objc private func updateRecordTitle() {
        let on = RecordingManager.shared.isRecording
        recordItem?.title = (on ? "Arrêter l'enregistrement" : "Démarrer l'enregistrement") + "   \(CaptureHotkey.label(for: CaptureHotkey.record))"
    }

    @objc private func openWindow() { onOpenWindow?() }
    @objc private func region() { onRegion?() }
    @objc private func window() { onWindow?() }
    @objc private func full() { onFull?() }
    @objc private func regionClip() { onRegionClipboard?() }
    @objc private func toggleRecord() { onToggleRecord?() }
    @objc private func recordRegion() { onRecordRegion?() }
    @objc private func openFolder() { onOpenFolder?() }
    @objc private func preferences() { onPreferences?() }
    @objc private func about() { onAbout?() }
}
