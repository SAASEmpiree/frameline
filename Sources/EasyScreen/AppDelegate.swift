import AppKit
import Carbon.HIToolbox

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusBar: StatusBarController!
    private var prefsController: PreferencesWindowController?

    func applicationWillFinishLaunching(_ notification: Notification) {
        // Le handler d'URL DOIT être posé AVANT didFinishLaunching : un lancement à froid via
        // easyscreen:// délivre l'Apple Event GetURL pendant le démarrage, sinon il est perdu.
        registerURLHandler()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        ESLog.installObservers()
        ESLog.snapshot("applicationDidFinishLaunching")
        ThemeManager.apply()                       // applique le thème choisi (défaut : sombre)
        Preferences.shared.ensureSaveDirectory()
        Notifier.requestAuth()

        statusBar = StatusBarController()
        wireMenu()
        registerHotKeys()
        // Pas de demande d'autorisation au lancement : on la demande seulement à la 1re capture
        // (via CaptureManager.ensureScreenRecordingPermission) → aucune invite surprise au démarrage.

        // Bibliothèque de captures + UI fenêtrée.
        CaptureLibrary.shared.start()
        AppCoordinator.shared.onOpenPreferences = { [weak self] in self?.showPreferences() }
        AppCoordinator.shared.onAnnotate = { url in PreviewWindowController.show(imageURL: url) }

        // Premier lancement → tutoriel. Sinon : démarrage SILENCIEUX en .accessory (agent menubar),
        // AUCUNE fenêtre ouverte, AUCUN focus volé — exactement comme BridgeShot. Ouvrir la galerie
        // ici la faisait passer en .regular + NSApp.activate(ignoringOtherApps:true) → l'app volait le
        // focus à Finder AU LANCEMENT et restait .regular À VIE (cf. windowWillClose), d'où le « retour
        // de fenêtre » à chaque capture. La galerie ne s'ouvre QUE sur action explicite (menubar
        // « Ouvrir Easy Screen », barre d'outils, réouverture, fin d'onboarding).
        if !Preferences.shared.onboardingCompleted {
            OnboardingWindowController.shared.onComplete = { MainWindowController.shared.show() }
            OnboardingWindowController.shared.show()
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        ESLog.snapshot("applicationShouldHandleReopen flag=\(flag)")
        ESLog.stack("applicationShouldHandleReopen")
        // Ne JAMAIS ouvrir la galerie (qui repasse l'app en .regular + NSApp.activate) pendant qu'un
        // overlay de capture est à l'écran : un reopen parasite volerait le focus / ramènerait une
        // fenêtre en pleine sélection. Hors capture, la réouverture volontaire reste normale.
        guard !RegionSelectOverlay.shared.isPresenting,
              !CaptureOverlayController.shared.isPresenting,
              !CaptureWindowOverlayController.shared.isPresenting else { return true }
        MainWindowController.shared.show()
        return true
    }

    func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool { true }

    /// Si un enregistrement est en cours au moment de quitter, on le finalise (SIGINT) d'abord :
    /// sinon le .mov resterait corrompu et le process screencapture deviendrait orphelin.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard RecordingManager.shared.isRecording else { return .terminateNow }
        var replied = false
        var token: NSObjectProtocol?
        func finish() {
            guard !replied else { return }
            replied = true
            if let token { NotificationCenter.default.removeObserver(token) }
            NSApp.reply(toApplicationShouldTerminate: true)
        }
        token = NotificationCenter.default.addObserver(forName: .recordingStateChanged,
                                                       object: nil, queue: .main) { _ in
            if !RecordingManager.shared.isRecording { finish() }
        }
        RecordingManager.shared.stop()
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { finish() }   // garde-fou
        return .terminateLater
    }

    // MARK: - Câblage

    private func wireMenu() {
        statusBar.onOpenWindow = { MainWindowController.shared.show() }
        statusBar.onRegion = { [weak self] in self?.doRegion() }
        statusBar.onWindow = { [weak self] in self?.doWindow() }
        statusBar.onFull = { [weak self] in self?.doFull() }
        statusBar.onRegionClipboard = { CaptureManager.shared.capture(.region, toClipboard: true) }
        statusBar.onToggleRecord = { RecordingManager.shared.toggleFullScreen() }
        statusBar.onRecordRegion = { RecordingManager.shared.startRegion() }
        statusBar.onOpenFolder = { NSWorkspace.shared.open(Preferences.shared.saveDirectory) }
        statusBar.onPreferences = { [weak self] in self?.showPreferences() }
        statusBar.onAbout = { [weak self] in self?.showAbout() }
    }

    private func registerHotKeys() {
        HotKeyManager.shared.registerDefaults(
            region: { [weak self] in self?.doRegion() },
            window: { [weak self] in self?.doWindow() },
            full:   { [weak self] in self?.doFull() },
            record: { RecordingManager.shared.toggleFullScreen() }
        )
    }

    // MARK: - Schéma d'URL easyscreen:// (via Apple Event kAEGetURL — pas le delegate iOS)

    private func registerURLHandler() {
        NSAppleEventManager.shared().setEventHandler(
            self, andSelector: #selector(handleGetURL(_:withReplyEvent:)),
            forEventClass: AEEventClass(kInternetEventClass),
            andEventID: AEEventID(kAEGetURL))
    }

    @objc private func handleGetURL(_ event: NSAppleEventDescriptor, withReplyEvent reply: NSAppleEventDescriptor) {
        guard let str = event.paramDescriptor(forKeyword: AEKeyword(keyDirectObject))?.stringValue,
              let url = URL(string: str) else { return }
        switch url.host {
        case "region": doRegion()
        case "window": doWindow()
        case "full":   doFull()
        case "record": RecordingManager.shared.toggleFullScreen()
        default: break
        }
    }

    // MARK: - Actions

    // Les captures passent par le coordinateur → elles atterrissent dans la galerie + fenêtre.
    private func doRegion() { ESLog.snapshot("doRegion (hotkey/menu)"); AppCoordinator.shared.captureRegion() }
    private func doWindow() { ESLog.snapshot("doWindow (hotkey/menu)"); AppCoordinator.shared.captureWindow() }
    private func doFull()   { ESLog.snapshot("doFull (hotkey/menu)"); AppCoordinator.shared.captureFull() }

    private func showPreferences() {
        if prefsController == nil { prefsController = PreferencesWindowController() }
        prefsController?.reload()        // resynchronise les contrôles (ex. lancement au démarrage)
        NSApp.activate(ignoringOtherApps: true)
        prefsController?.showWindow(nil)
        prefsController?.window?.makeKeyAndOrderFront(nil)
    }

    private func showAbout() {
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = "Easy Screen"
        a.informativeText = "Capture d'écran et enregistrement, simples et rapides.\nVersion 1.0.0\n© 2026 SAAS Empire."
        a.addButton(withTitle: "OK")
        a.runModal()
    }
}
