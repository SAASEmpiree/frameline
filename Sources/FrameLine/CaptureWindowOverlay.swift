import AppKit
import CoreGraphics
import ScreenCaptureKit

/// Overlay de SÉLECTION DE FENÊTRE (mode ⇧⌘!), façon BridgeShot / capture native macOS.
///
/// POURQUOI un overlay maison plutôt que `screencapture -i -w` — on veut rester 100 % in-process
/// avec ScreenCaptureKit (cohérent avec `ScreenCapturer`) : la responsabilité TCC reste sans
/// ambiguïté FrameLine, et l'image revient en `CGImage` (pas de fichier intermédiaire ni de
/// process séparé qui désactiverait l'app). On énumère les fenêtres à l'écran, on assombrit tout
/// l'écran et on « perce » un trou net sur la fenêtre sous le curseur (surbrillance) ; le clic
/// capture cette unique fenêtre via `SCContentFilter(desktopIndependentWindow:)` — qui isole la
/// fenêtre CIBLE indépendamment du z-order, donc notre propre overlay n'apparaît JAMAIS dans le
/// résultat même s'il est au-dessus. Échap annule.
///
/// Ce contrôleur ne fait QUE produire le `CGImage` ; l'écriture disque, la galerie, la
/// notification et l'éditeur restent du ressort de l'orchestrateur (AppCoordinator) via la
/// `completion` — même découpage que le reste de l'app.
@MainActor
final class CaptureWindowOverlayController {
    static let shared = CaptureWindowOverlayController()
    private init() {}

    // Un overlay sombre PAR écran (couvre aussi la barre des menus). Retenus fortement le temps
    // de la session ; vidés au teardown → libère fenêtres + vues.
    private var overlayWindows: [WindowOverlayWindow] = []
    // Fenêtres éligibles dans l'ordre AVANT→ARRIÈRE (z-order) renvoyé par SCK, pour résoudre le
    // chevauchement : la première qui contient le curseur est la fenêtre du dessus.
    private var candidates: [SCWindow] = []
    private var hoveredWindowID: CGWindowID?
    private var completion: ((CGImage?) -> Void)?
    /// Vrai tant que l'overlay de sélection de fenêtre est à l'écran (garde anti-reopen).
    var isPresenting: Bool { !overlayWindows.isEmpty }
    private var keyMonitor: Any?
    private var finished = false
    /// App active AVANT l'overlay : on lui rend le focus à la fin (ne jamais le voler).
    private var previousApp: NSRunningApplication?
    /// Bascule de mode (Espace) : l'orchestrateur y branche le démarrage de la capture ZONE.
    var onSwitchMode: (() -> Void)?

    /// Démarre la sélection de fenêtre. `completion` reçoit le `CGImage` capturé, ou `nil` si
    /// l'utilisateur annule (Échap / clic dans le vide impossible), si l'autorisation manque ou en
    /// cas d'erreur. Toujours appelée EXACTEMENT une fois, sur le main.
    @MainActor
    func beginWindow(completion: @escaping (CGImage?) -> Void) {
        // Anti-réentrance : une seule session d'overlay vivante à la fois.
        guard overlayWindows.isEmpty, self.completion == nil else { completion(nil); return }
        // Sans l'autorisation « Enregistrement de l'écran », SCK ne livrerait que le fond d'écran :
        // on réutilise la garde TCC existante (prompt système / NSAlert au plus une fois/session).
        guard CaptureManager.shared.ensureScreenRecordingPermission() else { completion(nil); return }

        self.completion = completion
        self.finished = false
        self.previousApp = NSWorkspace.shared.frontmostApplication   // app où était l'utilisateur

        // Énumération asynchrone (hérite du MainActor) → on bascule l'UI dès la liste obtenue.
        Task {
            do {
                let content = try await ScreenCapturer.shareableContent()
                self.start(with: content)
            } catch {
                NSLog("FrameLine — énumération des fenêtres échouée: \(error.localizedDescription)")
                self.complete(nil)
            }
        }
    }

    // MARK: - Mise en place de l'overlay

    private func start(with content: SCShareableContent) {
        let myPID = ProcessInfo.processInfo.processIdentifier
        // Filtre : à l'écran, pas les nôtres, calque NORMAL (0) → écarte barre des menus, Dock,
        // bureau, infobulles ; taille mini → écarte les bulles/HUD minuscules.
        candidates = content.windows.filter { w in
            w.isOnScreen
                && w.owningApplication?.processID != myPID
                && w.windowLayer == 0
                && w.frame.width >= 40 && w.frame.height >= 40
        }
        guard !candidates.isEmpty else { complete(nil); return }   // rien de sélectionnable

        for screen in NSScreen.screens {
            let view = WindowOverlayView(frame: NSRect(origin: .zero, size: screen.frame.size))
            view.owner = self

            // Borderless transparent : on peint nous-mêmes l'assombrissement et le « trou » net.
            let w = WindowOverlayWindow(contentRect: screen.frame,
                                        styleMask: [.borderless, .nonactivatingPanel],
                                        backing: .buffered, defer: false)
            w.isOpaque = false
            w.backgroundColor = .clear
            w.hasShadow = false
            w.acceptsMouseMovedEvents = true
            w.ignoresMouseEvents = false
            w.hidesOnDeactivate = false                            // app inactive : ne pas masquer l'overlay
            // Niveau « bouclier » : au-dessus de TOUT (y compris barre des menus) → l'overlay
            // recouvre l'écran entier sans qu'aucune fenêtre ne passe devant.
            w.level = NSWindow.Level(rawValue: Int(CGShieldingWindowLevel()))
            w.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
            w.contentView = view
            overlayWindows.append(w)
        }
        // Aucun écran (clapet fermé sans moniteur, reconfiguration en cours) : sans overlay, ni clic ni
        // Échap ne pourraient jamais appeler complete() → `completion` resterait posée et TOUTES les
        // sessions suivantes seraient refusées par la garde anti-réentrance de beginWindow.
        guard !overlayWindows.isEmpty else { complete(nil); return }

        // Les panneaux non-activants s'affichent sur le Space COURANT (plein écran inclus) via
        // orderFrontRegardless(). On ACTIVE ENSUITE l'app — indispensable : macOS ne livre les
        // ÉVÉNEMENTS CLAVIER (Échap/Espace, via le moniteur local + keyDown) qu'à l'app ACTIVE, même
        // pour un panneau « key ». Sans cette activation, l'overlay fenêtre déclenché par raccourci
        // global au-dessus d'une AUTRE app restait bloqué (Échap/Espace morts). Parité avec l'overlay
        // ZONE (CaptureOverlay.present). canBecomeMain=false → aucune fenêtre ne « revient », et les
        // overlays sont déjà sur le Space courant → pas de bascule de Space. Le focus est rendu à
        // l'app précédente dans complete().
        let mouse = NSEvent.mouseLocation
        // NSMouseInRect (et non `contains`) : le curseur collé en HAUT d'un écran a y == frame.maxY,
        // que `contains` exclut → mauvais écran (ou l'écran du dessus, s'il est empilé).
        let keyWindow = overlayWindows.first { NSMouseInRect(mouse, $0.frame, false) } ?? overlayWindows.first
        overlayWindows.forEach { $0.orderFrontRegardless() }
        keyWindow?.makeKey()
        if let view = keyWindow?.contentView { keyWindow?.makeFirstResponder(view) }
        NSApp.activate(ignoringOtherApps: true)

        installKeyMonitor()
        updateHover()                                              // surligne d'emblée la fenêtre sous le curseur
    }

    private func installKeyMonitor() {
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            switch event.keyCode {
            case 53: MainActor.assumeIsolated { self?.complete(nil) }; return nil          // Échap = annuler
            case 49: MainActor.assumeIsolated { self?.switchModeRequested() }; return nil  // Espace = bascule mode zone
            default: return event
            }
        }
    }

    // MARK: - Survol (appelé par la vue, sur le main)

    fileprivate func updateHover() {
        guard !overlayWindows.isEmpty else { return }
        let mouse = NSEvent.mouseLocation                          // Cocoa global (origine bas-gauche)
        let cg = CGPoint(x: mouse.x, y: primaryHeight - mouse.y)   // → CG global (origine haut-gauche)
        let hit = candidates.first { $0.frame.contains(cg) }
        guard hit?.windowID != hoveredWindowID else { return }     // pas de changement → évite un redraw inutile
        hoveredWindowID = hit?.windowID
        refreshHighlight(hit)
    }

    private func refreshHighlight(_ window: SCWindow?) {
        // Libellé « Titre — App » de la fenêtre survolée (parité BridgeShot : on annonce ce qui sera capturé).
        let label: String? = window.map { w in
            let title = (w.title ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            let app = w.owningApplication?.applicationName ?? ""
            return [title, app].filter { !$0.isEmpty }.joined(separator: "  —  ")
        }
        for w in overlayWindows {
            guard let view = w.contentView as? WindowOverlayView else { continue }
            if let cg = window?.frame {
                // CG global → Cocoa global → coordonnées LOCALES de cet overlay (la fenêtre cible
                // peut chevaucher deux écrans : chaque overlay clippe sa part → surbrillance continue).
                let cocoa = cocoaGlobalRect(cg)
                let localRect = cocoa.offsetBy(dx: -w.frame.minX, dy: -w.frame.minY)
                view.highlightRect = localRect
                // La pilule « Titre — App » n'apparaît QUE sur l'écran qui contient (une partie de) la
                // fenêtre survolée : sinon le clamp horizontal de drawWindowLabel la redessinerait sur un
                // moniteur voisin où rien n'est surligné.
                view.highlightLabel = (label?.isEmpty == false && localRect.intersects(view.bounds)) ? label : nil
            } else {
                view.highlightRect = nil
                view.highlightLabel = nil
            }
            view.needsDisplay = true
        }
    }

    // MARK: - Clic → capture (appelé par la vue, sur le main)

    /// Annulation Échap : filet de sécurité depuis la vue first responder, en complément du moniteur local.
    fileprivate func cancel() { complete(nil) }

    /// Espace : referme cet overlay puis démarre la capture ZONE via l'orchestrateur.
    fileprivate func switchModeRequested() {
        let cb = onSwitchMode
        complete(nil)
        cb?()
    }

    fileprivate func handleClick() {
        // Clic dans le vide (aucune fenêtre survolée) → on ignore ; Échap reste la voie d'annulation.
        guard let id = hoveredWindowID,
              let target = candidates.first(where: { $0.windowID == id }) else { return }

        teardownUI()                                               // masque l'overlay AVANT la capture (rendu instantané)
        Task {
            do {
                let image = try await self.capture(target)
                self.complete(image)
            } catch {
                NSLog("FrameLine — capture fenêtre échouée: \(error.localizedDescription)")
                self.complete(nil)
            }
        }
    }

    private func capture(_ window: SCWindow) async throws -> CGImage {
        // `desktopIndependentWindow` isole la fenêtre cible (z-order ignoré) → notre overlay,
        // même au-dessus, est exclu du rendu : aucun risque de capturer l'assombrissement.
        let filter = SCContentFilter(desktopIndependentWindow: window)
        let cfg = SCStreamConfiguration()
        cfg.showsCursor = false
        cfg.captureResolution = .best
        // Ombre portée selon la préférence (équivalent `screencapture -w` vs `-o`).
        cfg.ignoreShadowsSingleWindow = !Preferences.shared.windowShadow
        // Pixels NATIFS = points du contenu × échelle Retina → image nette 1:1.
        let scale = CGFloat(filter.pointPixelScale)
        cfg.width = max(1, Int((filter.contentRect.width * scale).rounded()))
        cfg.height = max(1, Int((filter.contentRect.height * scale).rounded()))
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: cfg)
    }

    // MARK: - Fin / nettoyage

    private func complete(_ image: CGImage?) {
        guard !finished else { return }                            // garantit un seul appel à completion
        finished = true
        teardownUI()
        // Rendre le focus à l'app où l'utilisateur était AVANT la capture (parité avec la région ;
        // ne jamais voler le focus). Uniquement si la capture l'a réellement pris (NSApp.isActive) —
        // sinon un prev.activate() superflu rejouerait une transition de Space. La bascule de mode
        // (Espace) réactivera FrameLine via le nouvel overlay (qui enregistre son propre previousApp).
        if NSApp.isActive, let prev = previousApp,
           prev.processIdentifier != getpid(), !prev.isTerminated {
            prev.activate()
        }
        previousApp = nil
        let c = completion
        completion = nil
        c?(image)
    }

    private func teardownUI() {
        if let m = keyMonitor { NSEvent.removeMonitor(m); keyMonitor = nil }
        // Le teardown part souvent de `mouseDown` d'une vue d'overlay (handleClick) : relâcher les
        // fenêtres ICI les désallouerait (vue comprise) pendant que -[NSWindow sendEvent:] est encore
        // sur la pile → accès à un objet libéré. On les garde en vie jusqu'au tour de run-loop suivant.
        let closing = overlayWindows
        closing.forEach { $0.orderOut(nil) }
        overlayWindows.removeAll()
        DispatchQueue.main.async { withExtendedLifetime(closing) {} }
        candidates.removeAll()
        hoveredWindowID = nil
        NSCursor.arrow.set()                          // restaure le curseur (jamais de réticule collé)
    }

    // MARK: - Conversion de repères

    /// Hauteur de l'écran PRIMAIRE (origine Cocoa (0,0)) = pivot du basculement Cocoa↔CG.
    private var primaryHeight: CGFloat {
        NSScreen.screens.first(where: { $0.frame.origin == .zero })?.frame.height
            ?? NSScreen.main?.frame.height ?? 0
    }

    /// Rect CG global (haut-gauche, comme `SCWindow.frame`) → rect Cocoa global (bas-gauche).
    private func cocoaGlobalRect(_ cg: CGRect) -> CGRect {
        CGRect(x: cg.origin.x, y: primaryHeight - cg.maxY, width: cg.width, height: cg.height)
    }
}

// MARK: - Fenêtre d'overlay

/// Panneau borderless NON-ACTIVANT : s'affiche sur le Space courant (y compris au-dessus d'une app en
/// plein écran) sans activer FrameLine ni basculer de Space. Doit pouvoir devenir key pour router
/// Échap vers le moniteur local.
private final class WindowOverlayWindow: NSPanel {
    override var canBecomeKey: Bool { true }
    // canBecomeMain=false : devenir MAIN activerait l'app (vol de focus / switch de Space). Échap
    // passe déjà par installKeyMonitor() + keyDown, donc le panneau n'a jamais besoin de devenir main.
    override var canBecomeMain: Bool { false }
}

// MARK: - Vue d'overlay

/// Assombrit tout l'écran et « perce » un trou net sur la fenêtre survolée (surbrillance).
/// Non isolée @MainActor (sous-classe AppKit) : les événements arrivent déjà sur le main, on
/// rebascule vers le contrôleur @MainActor via `assumeIsolated`.
private final class WindowOverlayView: NSView {
    weak var owner: CaptureWindowOverlayController?
    /// Rect (coordonnées LOCALES de la vue) de la fenêtre à surligner ; `nil` = tout assombri.
    var highlightRect: NSRect?
    /// Libellé « Titre — App » de la fenêtre survolée (affiché en pilule près du cadre).
    var highlightLabel: String?
    private let dimAlpha: CGFloat = 0.28
    private let hintBar = GlassHintBar(segments: [
        .init(label: "Cliquez une fenêtre"),
        .init(key: "Espace", label: "Mode zone"),
        .init(key: "Échap", label: "Annuler"),
    ])

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        addSubview(hintBar)
        positionHintBar()
    }
    @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        positionHintBar()
    }
    private func positionHintBar() {
        let s = hintBar.barSize
        hintBar.frame = NSRect(x: ((bounds.width - s.width) / 2).rounded(), y: 48,
                               width: s.width, height: s.height)
    }

    override var isOpaque: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }   // clic capturant même app inactive
    override var acceptsFirstResponder: Bool { true }                       // first responder → Échap en filet

    // Filet de sécurité Échap : si le panneau non-activant est key, le keyDown arrive aussi ici (en
    // plus du moniteur local du contrôleur). 53 = Échap.
    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 53: MainActor.assumeIsolated { owner?.cancel() }                  // Échap = annuler
        case 49: MainActor.assumeIsolated { owner?.switchModeRequested() }     // Espace = bascule mode zone
        default: super.keyDown(with: event)
        }
    }

    override func cancelOperation(_ sender: Any?) {
        MainActor.assumeIsolated { owner?.cancel() }
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        ctx.setFillColor(NSColor.black.withAlphaComponent(dimAlpha).cgColor)
        ctx.fill(bounds)
        guard let hole = highlightRect else { return }
        ctx.clear(hole)                                            // perce → la fenêtre survolée apparaît nette
        // Liseré d'accent (façon surbrillance macOS) ; inset pour rester collé au bord de la fenêtre.
        ctx.setStrokeColor(NSColor.controlAccentColor.cgColor)
        ctx.setLineWidth(2)
        ctx.stroke(hole.insetBy(dx: 1, dy: 1))
        if let label = highlightLabel { drawWindowLabel(label, for: hole) }
    }

    /// Attributs du libellé, construits UNE fois : recréer la police à chaque dessin expose à la course
    /// CoreText qui fait planter la loupe (cf. OverlayText) — `draw` repasse à chaque changement de survol.
    private static let labelAttributes: [NSAttributedString.Key: Any] = [
        .font: NSFont.systemFont(ofSize: 12, weight: .semibold),
        .foregroundColor: NSColor.white,
    ]

    /// Pilule sombre « Titre — App » centrée sous (ou dans) le cadre de la fenêtre survolée.
    private func drawWindowLabel(_ text: String, for hole: NSRect) {
        let str = NSAttributedString(string: text, attributes: Self.labelAttributes)
        let ts = str.size()
        let padX: CGFloat = 10, padY: CGFloat = 5
        let pillW = min(ts.width + padX * 2, bounds.width - 16)
        let pillH = ts.height + padY * 2
        var x = hole.midX - pillW / 2
        x = min(max(x, bounds.minX + 8), bounds.maxX - pillW - 8)
        var y = hole.minY - 8 - pillH                 // sous le cadre (origine bas-gauche → en-dessous)
        if y < bounds.minY + 8 { y = min(hole.minY + 8, bounds.maxY - pillH - 8) }   // sinon à l'intérieur, en bas
        let pill = NSRect(x: x.rounded(), y: y.rounded(), width: pillW.rounded(), height: pillH.rounded())
        NSColor(white: 0, alpha: 0.82).setFill()
        NSBezierPath(roundedRect: pill, xRadius: 7, yRadius: 7).fill()
        str.draw(at: NSPoint(x: pill.minX + padX, y: pill.minY + padY))
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        // .inVisibleRect → suit automatiquement toute la vue ; .activeAlways → mouseMoved même non-key.
        addTrackingArea(NSTrackingArea(rect: .zero,
                                       options: [.activeAlways, .mouseMoved, .inVisibleRect],
                                       owner: self))
    }

    override func mouseMoved(with event: NSEvent) {
        MainActor.assumeIsolated { owner?.updateHover() }
    }

    override func mouseDown(with event: NSEvent) {
        MainActor.assumeIsolated { owner?.handleClick() }
    }
}
