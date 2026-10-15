import AppKit
import Carbon.HIToolbox

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusBar: StatusBarController!
    private var prefsController: PreferencesWindowController?

    func applicationWillFinishLaunching(_ notification: Notification) {
        // Le handler d'URL DOIT être posé AVANT didFinishLaunching : un lancement à froid via
        // frameline:// délivre l'Apple Event GetURL pendant le démarrage, sinon il est perdu.
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

        // Serveur MCP local (127.0.0.1 + jeton) : Coucou, Claude Code, Cursor… pilotent FrameLine.
        MCPController.shared.startIfEnabled()

        // Premier lancement → tutoriel. Sinon : démarrage SILENCIEUX en .accessory (agent menubar),
        // AUCUNE fenêtre ouverte, AUCUN focus volé — exactement comme BridgeShot. Ouvrir la galerie
        // ici la faisait passer en .regular + NSApp.activate(ignoringOtherApps:true) → l'app volait le
        // focus à Finder AU LANCEMENT et restait .regular À VIE (cf. windowWillClose), d'où le « retour
        // de fenêtre » à chaque capture. La galerie ne s'ouvre QUE sur action explicite (menubar
        // « Ouvrir FrameLine », barre d'outils, réouverture, fin d'onboarding).
        if !Preferences.shared.onboardingCompleted {
            OnboardingWindowController.shared.onComplete = { MainWindowController.shared.show() }
            // Ne PAS construire la fenêtre d'onboarding (NSWindow + collectionBehavior +
            // makeKeyAndOrderFront + NSHostingController<OnboardingView>) de façon SYNCHRONE ici : au
            // TOUT premier lancement APRÈS un redémarrage (login item lancé tôt au boot), la session
            // WindowServer n'est pas encore établie → -[NSWindow _validateCollectionBehavior:] lève une
            // NSException non rattrapée → abort(). Pire : comme le crash survient AVANT que
            // onboardingCompleted passe à true, l'onboarding se rejoue et l'app reboucle en crash à
            // chaque lancement. On diffère au tour de run-loop suivant, quand la session est prête.
            DispatchQueue.main.async {
                OnboardingWindowController.shared.show()
            }
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

    /// Quitter : le serveur MCP cesse d'écouter et son fichier de découverte disparaît.
    func applicationWillTerminate(_ notification: Notification) {
        MCPController.shared.shutdown()
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
            MainActor.assumeIsolated {
                if !RecordingManager.shared.isRecording { finish() }
            }
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
        // Nouveaux items « façon BridgeShot »
        statusBar.onFullDelayed = { secs in AppCoordinator.shared.captureFullDelayed(secs) }
        statusBar.onPinFromClipboard = { AppCoordinator.shared.pinFromClipboard() }
        statusBar.onCaptureHistory = { CaptureHistoryController.shared.toggle() }
        statusBar.onAllCaptures = { MainWindowController.shared.show() }        // « ouvre l'app directement »
        statusBar.onSetupGuide = {
            OnboardingWindowController.shared.onComplete = { MainWindowController.shared.show() }
            OnboardingWindowController.shared.show()
        }
        statusBar.onCheckUpdates = { [weak self] in self?.showUpToDate() }
    }

    /// « Check for Updates… » — FrameLine n'a pas (encore) de canal de mise à jour : on confirme
    /// simplement que la version installée est à jour (placeholder honnête, localisé via I18n).
    private func showUpToDate() { esShowUpToDate() }

    private func registerHotKeys() {
        HotKeyManager.shared.registerDefaults(
            region: { [weak self] in self?.doRegion() },
            window: { [weak self] in self?.doWindow() },
            full:   { [weak self] in self?.doFull() },
            record: { RecordingManager.shared.toggleFullScreen() }
        )
    }

    // MARK: - Schéma d'URL frameline:// (via Apple Event kAEGetURL — pas le delegate iOS)

    private func registerURLHandler() {
        NSAppleEventManager.shared().setEventHandler(
            self, andSelector: #selector(handleGetURL(_:withReplyEvent:)),
            forEventClass: AEEventClass(kInternetEventClass),
            andEventID: AEEventID(kAEGetURL))
    }

    @objc private func handleGetURL(_ event: NSAppleEventDescriptor, withReplyEvent reply: NSAppleEventDescriptor) {
        guard let str = event.paramDescriptor(forKeyword: AEKeyword(keyDirectObject))?.stringValue,
              let url = URL(string: str) else { return }
        // [SEC] Le schéma frameline:// est ouvrable par une page web (depuis un navigateur) ou par
        // n'importe quel process local. Comme FrameLine détient la permission TCC « Enregistrement
        // de l'écran », toute action de CAPTURE/ENREGISTREMENT déclenchée par URL exige désormais une
        // confirmation explicite : sinon un tiers pourrait filmer l'écran en silence en abusant de
        // notre permission (confused deputy). Les actions inertes (open/settings) restent directes.
        switch url.host {
        case "region": if confirmURLTrigger("la capture d'une région") { doRegion() }
        case "window": if confirmURLTrigger("la capture d'une fenêtre") { doWindow() }
        case "full":   if confirmURLTrigger("une capture plein écran") { doFull() }
        case "record":
            // Arrêter un enregistrement en cours est sans risque → pas de confirmation ; en
            // DÉMARRER un via URL exige l'accord de l'utilisateur.
            if RecordingManager.shared.isRecording {
                RecordingManager.shared.toggleFullScreen()
            } else if confirmURLTrigger("l'enregistrement de l'écran") {
                RecordingManager.shared.toggleFullScreen()
            }
        case "open":   doOpen()
        case "settings", "preferences": showPreferences()
        default: break
        }
    }

    /// [SEC] Alerte modale de confirmation pour une action sensible déclenchée par frameline://.
    private func confirmURLTrigger(_ action: String) -> Bool {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Autoriser \(action) ?"
        alert.informativeText = "Une application ou un site web vient de demander « \(action) » via frameline://. N'autorisez que si vous êtes à l'origine de cette action."
        alert.addButton(withTitle: "Autoriser")
        alert.addButton(withTitle: "Refuser")
        return alert.runModal() == .alertFirstButtonReturn
    }

    // MARK: - Actions

    // Les captures passent par le coordinateur → elles atterrissent dans la galerie + fenêtre.
    private func doRegion() { ESLog.snapshot("doRegion (hotkey/menu)"); AppCoordinator.shared.captureRegion() }
    private func doWindow() { ESLog.snapshot("doWindow (hotkey/menu)"); AppCoordinator.shared.captureWindow() }
    private func doFull()   { ESLog.snapshot("doFull (hotkey/menu)"); AppCoordinator.shared.captureFull() }
    // Ouvre la fenêtre principale sur la capture la plus récente (éditeur). Pratique
    // depuis un raccourci/URL — et sélectionne la plus récente si aucune n'est ouverte.
    private func doOpen() {
        if let recent = CaptureLibrary.shared.captures.first {
            AppCoordinator.shared.openInApp(recent.url)
        } else {
            MainWindowController.shared.show()
        }
    }

    private func showPreferences() {
        if prefsController == nil { prefsController = PreferencesWindowController() }
        prefsController?.reload()        // resynchronise les contrôles (ex. lancement au démarrage)
        guard let win = prefsController?.window else { return }
        // EXACTEMENT la recette de Flow (AppController.present) : la fenêtre s'ouvre sur l'écran + le
        // bureau (Space) ACTIF, sous le curseur, même par-dessus une app plein écran. L'ORDRE est clé :
        // collectionBehavior `.moveToActiveSpace` → recentrage sur l'écran actif → makeKeyAndOrderFront
        // AVANT NSApp.activate (app accessoire). C'est l'activation FINALE qui tire la fenêtre (déjà
        // ordonnée) sur le bureau courant — activer AVANT ne le faisait pas.
        win.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        if win.isMiniaturized { win.deminiaturize(nil) }
        win.centerOnActiveScreen()
        win.makeKeyAndOrderFront(nil)   // key AVANT activate (app accessoire)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func showAbout() {
        // Comme Flow et Units : « À propos » ouvre l'onglet About des Réglages (fenêtre
        // recentrée sur l'écran actif), au lieu d'une alerte non centrée sur notre écran.
        UserDefaults.standard.set("about", forKey: "settingsTab")
        showPreferences()
    }
}
