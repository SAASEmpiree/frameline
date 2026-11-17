import AppKit

/// Icône + menu dans la barre des menus — calqué sur BridgeShot (hormis le bloc « compte / Sign
/// out » : FrameLine est 100 % local, sans compte). Les libellés suivent la langue d'affichage
/// (I18n) et le menu se reconstruit à chaud quand elle change.
/// Toutes les actions sont déléguées via des closures câblées dans AppDelegate.
@MainActor
final class StatusBarController: NSObject, NSMenuItemValidation {
    private let statusItem: NSStatusItem
    private let menu = NSMenu()
    private var recordItem: NSMenuItem!
    private var recPulseTimer: Timer?          // pulsation du point rouge pendant l'enregistrement

    /// Bord droit COMMUN (en points, espace-texte du titre) où s'alignent TOUS les raccourcis affichés.
    /// Recalculé à chaque (re)construction du menu selon les libellés de la langue courante — voir
    /// computeShortcutTabStop(). Ainsi les raccourcis sont TOUJOURS collés à droite, sans nombre magique.
    private var shortcutTabStop: CGFloat = 232
    // Réglages / Quitter : on N'UTILISE PLUS `keyEquivalent` natif (il réserve une colonne à droite qui
    // ÉLARGIT le menu → les raccourcis « custom » des captures s'arrêtaient AVANT ce bord et ne
    // paraissaient pas collés à droite). On affiche donc leur raccourci comme les autres (texte gris
    // aligné à droite). Ces glyphes ⌘,/⌘Q ne dépendent pas de la disposition clavier (pas de bug AZERTY),
    // et le déclenchement clavier reste assuré par la barre de menus principale quand une fenêtre est ouverte.
    private let settingsHint = "⌘,"
    private let quitHint = "⌘Q"

    var onOpenWindow: (() -> Void)?
    var onRegion, onWindow, onFull, onRegionClipboard: (() -> Void)?
    var onToggleRecord, onRecordRegion, onOpenFolder, onPreferences, onAbout: (() -> Void)?
    var onAllCaptures, onCaptureHistory, onSetupGuide, onCheckUpdates, onPinFromClipboard: (() -> Void)?
    var onFullDelayed: ((Int) -> Void)?

    override init() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()
        if let button = statusItem.button {
            let img = NSImage(systemSymbolName: "camera.viewfinder", accessibilityDescription: "FrameLine")
            img?.isTemplate = true
            button.image = img
        }
        buildMenu()
        statusItem.menu = menu
        NotificationCenter.default.addObserver(self, selector: #selector(updateRecordTitle),
                                               name: .recordingStateChanged, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(rebuildMenu),
                                               name: .displayLanguageChanged, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(rebuildMenu),
                                               name: .hotkeysChanged, object: nil)
    }

    // MARK: - Construction du menu

    /// Reconstruit le menu (changement de langue ou de raccourci dans les Réglages).
    @objc private func rebuildMenu() { buildMenu() }

    private func buildMenu() {
        menu.removeAllItems()        // re-constructible (changement de langue)
        shortcutTabStop = computeShortcutTabStop()   // bord droit commun, mesuré sur les libellés courants

        // En-tête « FrameLine <version> » (bandeau produit, SANS compte ni Sign out).
        let header = NSMenuItem()
        let v = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0"
        header.attributedTitle = NSAttributedString(string: "FrameLine \(v)", attributes: [
            .font: NSFont.systemFont(ofSize: 11, weight: .semibold),
            .foregroundColor: NSColor.secondaryLabelColor])
        header.isEnabled = false
        menu.addItem(header)
        menu.addItem(.separator())

        // Capture
        addItem(L("m_capture_area"), "rectangle.dashed", #selector(region), key: CaptureHotkey.region)
        addItem(L("m_capture_window"), "macwindow", #selector(window), key: CaptureHotkey.window)
        addItem(L("m_capture_fullscreen"), "rectangle.inset.filled", #selector(full), key: CaptureHotkey.fullScreen)
        menu.addItem(.separator())

        // Enregistrement
        recordItem = addItem(L("m_start_recording"), "record.circle", #selector(toggleRecord), key: CaptureHotkey.record)
        menu.addItem(.separator())

        // Capture différée + épingler depuis le presse-papiers
        let delayed = NSMenuItem(title: L("m_capture_fullscreen_in"), action: nil, keyEquivalent: "")
        delayed.image = symbol("timer")
        let sub = NSMenu()
        for s in [3, 5, 10] {
            let it = NSMenuItem(title: String(format: L("m_seconds"), s), action: #selector(fullDelayed(_:)), keyEquivalent: "")
            it.target = self
            it.tag = s
            sub.addItem(it)
        }
        delayed.submenu = sub
        menu.addItem(delayed)
        addItem(L("m_pin_clipboard"), "pin", #selector(pinClipboard))
        menu.addItem(.separator())

        // Historique + bibliothèque
        addItem(L("m_capture_history"), "clock.arrow.circlepath", #selector(captureHistory))
        addItem(L("m_all_captures"), "rectangle.stack", #selector(allCaptures))
        menu.addItem(.separator())

        // Aide / réglages
        addItem(L("m_setup_guide"), "list.bullet.rectangle", #selector(setupGuide))
        addItem(L("m_check_updates"), "arrow.triangle.2.circlepath", #selector(checkUpdates))
        // Raccourci ⌘, affiché comme les captures (texte gris collé à droite), PAS en keyEquivalent natif.
        addItem(L("m_settings"), "gearshape", #selector(preferences), hint: settingsHint)

        // Quitter — MÊME section que Réglages (pas de séparateur). Icône « xmark.rectangle » comme Flow.
        // target nil → l'action remonte jusqu'à NSApp. Raccourci ⌘Q affiché en texte gris collé à droite.
        let quit = NSMenuItem(title: L("m_quit"),
                              action: #selector(NSApplication.terminate(_:)), keyEquivalent: "")
        quit.image = symbol("xmark.rectangle")
        quit.attributedTitle = shortcutTitle(L("m_quit"), quitHint)
        menu.addItem(quit)

        updateRecordTitle()
    }

    // MARK: - Helpers de construction

    private func symbol(_ name: String) -> NSImage? {
        let cfg = NSImage.SymbolConfiguration(pointSize: 14, weight: .regular)
        let img = NSImage(systemSymbolName: name, accessibilityDescription: nil)?.withSymbolConfiguration(cfg)
        img?.isTemplate = true
        return img
    }

    /// Ajoute un item icône + libellé ; si `key` est fourni, affiche ⇧⌘<touche> en TEXTE
    /// (aligné à droite, en gris — façon BridgeShot). On N'UTILISE PAS `keyEquivalent` pour ces
    /// raccourcis : le rendu natif re-mappe la touche via la disposition active et affiche le
    /// MAUVAIS glyphe AZERTY (⇧⌘ù / ⇧⌘1 / ⇧⌘Ç au lieu de ⇧⌘' / ⇧⌘! / ⇧⌘ç). Le déclenchement réel
    /// reste assuré par HotKeyManager (raccourcis globaux Carbon).
    @discardableResult
    private func addItem(_ title: String, _ sym: String, _ sel: Selector,
                         key: Hotkey? = nil, hint: String? = nil) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: sel, keyEquivalent: "")
        item.target = self
        item.image = symbol(sym)
        // `hint` explicite (⌘,) OU dérivé d'un raccourci de capture (⇧⌘' via la disposition active).
        if let hintText = hint ?? key.map({ CaptureHotkey.label(for: $0) }) {
            item.attributedTitle = shortcutTitle(title, hintText)
        }
        menu.addItem(item)
        return item
    }

    /// Bord droit commun (espace-texte) où s'alignent tous les raccourcis : le plus large des
    /// « titre + espace + raccourci », mais jamais moins que le plus long titre SANS raccourci (chevron
    /// de sous-menu inclus) — sinon les raccourcis s'arrêteraient avant le bord de ces items-là.
    private func computeShortcutTabStop() -> CGFloat {
        let font = NSFont.systemFont(ofSize: NSFont.systemFontSize)
        func w(_ s: String) -> CGFloat { ceil((s as NSString).size(withAttributes: [.font: font]).width) }
        let gap: CGFloat = 36        // écart minimal titre↔raccourci (celui de l'item le plus large)

        // Items PORTANT un raccourci → bord droit requis = titre + gap + raccourci.
        let pairs: [(String, String)] = [
            (L("m_capture_area"),       CaptureHotkey.label(for: CaptureHotkey.region)),
            (L("m_capture_window"),     CaptureHotkey.label(for: CaptureHotkey.window)),
            (L("m_capture_fullscreen"), CaptureHotkey.label(for: CaptureHotkey.fullScreen)),
            (L("m_start_recording"),    CaptureHotkey.label(for: CaptureHotkey.record)),
            (L("m_stop_recording"),     CaptureHotkey.label(for: CaptureHotkey.record)),
            (L("m_settings"),           settingsHint),
            (L("m_quit"),               quitHint),
        ]
        var maxRight: CGFloat = 0
        for (t, h) in pairs { maxRight = max(maxRight, w(t) + gap + w(h)) }

        // Items SANS raccourci : le menu ne doit pas dépasser leur largeur (marge droite pour chevron).
        for t in [L("m_capture_fullscreen_in"), L("m_pin_clipboard"), L("m_capture_history"),
                  L("m_all_captures"), L("m_setup_guide"), L("m_check_updates")] {
            maxRight = max(maxRight, w(t) + 24)
        }
        return maxRight
    }

    /// « Libellé ............ ⇧⌘' » : titre à gauche, raccourci aligné à droite en gris.
    private func shortcutTitle(_ title: String, _ hint: String) -> NSAttributedString {
        let p = NSMutableParagraphStyle()
        p.tabStops = [NSTextTab(textAlignment: .right, location: shortcutTabStop)]
        let s = NSMutableAttributedString(string: title + "\t" + hint)
        let full = NSRange(location: 0, length: s.length)
        s.addAttribute(.paragraphStyle, value: p, range: full)
        s.addAttribute(.font, value: NSFont.systemFont(ofSize: NSFont.systemFontSize), range: full)
        s.addAttribute(.foregroundColor, value: NSColor.secondaryLabelColor,
                       range: NSRange(location: title.count + 1, length: hint.count))
        return s
    }

    @objc private func updateRecordTitle() {
        let on = RecordingManager.shared.isRecording
        let title = L(on ? "m_stop_recording" : "m_start_recording")
        recordItem?.attributedTitle = shortcutTitle(title, CaptureHotkey.label(for: CaptureHotkey.record))
        recordItem?.image = symbol(on ? "stop.circle" : "record.circle")
        updateStatusIcon(recording: on)
    }

    /// Reflète l'état d'enregistrement DIRECTEMENT dans la barre de menus : pendant l'enregistrement,
    /// l'icône devient un point d'enregistrement plein (`record.circle.fill`) qui PULSE doucement → on voit
    /// en permanence que ça enregistre. À l'arrêt, retour à l'icône neutre (camera.viewfinder).
    ///
    /// AUCUNE couleur forcée (pas de `contentTintColor`) : l'icône reste un TEMPLATE, donc macOS la teinte
    /// selon l'apparence de la barre de menus → NOIRE sur la barre normale (bureau clair) et BLANCHE quand
    /// on est sur une app en plein écran (barre sombre). Le rouge forcé cassait cette adaptation et
    /// paraissait noir/illisible sur une barre sombre.
    private func updateStatusIcon(recording: Bool) {
        guard let button = statusItem.button else { return }
        button.contentTintColor = nil                    // template pur → suit l'apparence (noir/blanc), jamais figé
        button.image = symbol(recording ? "record.circle.fill" : "camera.viewfinder")
        if recording { startRecPulse() } else { stopRecPulse() }
    }

    private func startRecPulse() {
        recPulseTimer?.invalidate()
        // .common → continue de pulser même pendant un suivi d'événements (menu ouvert, drag…).
        let t = Timer(timeInterval: 0.75, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let button = self?.statusItem.button else { return }
                NSAnimationContext.runAnimationGroup { ctx in
                    ctx.duration = 0.7
                    ctx.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
                    button.animator().alphaValue = button.alphaValue > 0.7 ? 0.4 : 1.0   // fondu 1 ↔ 0.4
                }
            }
        }
        RunLoop.main.add(t, forMode: .common)
        recPulseTimer = t
    }

    private func stopRecPulse() {
        recPulseTimer?.invalidate()
        recPulseTimer = nil
        statusItem.button?.alphaValue = 1.0              // repasse pleinement opaque
    }

    deinit { recPulseTimer?.invalidate() }

    // MARK: - Validation

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        if menuItem.action == #selector(pinClipboard) {
            return NSPasteboard.general.canReadObject(forClasses: [NSImage.self], options: nil)
        }
        return true
    }

    // MARK: - Actions (déléguées)

    @objc private func region() { onRegion?() }
    @objc private func window() { onWindow?() }
    @objc private func full() { onFull?() }
    @objc private func fullDelayed(_ sender: NSMenuItem) { onFullDelayed?(sender.tag) }
    @objc private func toggleRecord() { onToggleRecord?() }
    @objc private func pinClipboard() { onPinFromClipboard?() }
    @objc private func captureHistory() { onCaptureHistory?() }
    @objc private func allCaptures() { onAllCaptures?() }
    @objc private func setupGuide() { onSetupGuide?() }
    @objc private func checkUpdates() { onCheckUpdates?() }
    @objc private func preferences() { onPreferences?() }
}
